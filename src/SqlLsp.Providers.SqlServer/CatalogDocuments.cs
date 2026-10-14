using System.Collections.Immutable;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SqlLsp.Core;

namespace SqlLsp.Providers.SqlServer;

public sealed record CatalogNavigationSnapshot(CatalogSnapshot Catalog, ImmutableArray<DocumentSnapshot> Documents,
    ImmutableArray<IndexedSqlSymbol> Symbols);

// Only this process-owned private tree is written; catalog names never become filesystem paths.
public sealed class CatalogDocuments : IDisposable
{
    public string Root { get; }
    private readonly Queue<string> generations = new();
    private bool disposed;

    public CatalogDocuments()
    {
        Root = Path.Combine(Path.GetTempPath(), "sqlscriptdom-lsp-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).Create(security);
        }
        else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public bool Owns(Uri uri) => uri.IsFile && Path.GetFullPath(uri.LocalPath).StartsWith(Root + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<CatalogNavigationSnapshot> BuildAsync(CatalogSnapshot catalog, ParserOptions parserOptions, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var documents = ImmutableArray.CreateBuilder<DocumentSnapshot>();
        var symbols = ImmutableArray.CreateBuilder<IndexedSqlSymbol>();
        try
        {
            foreach (var obj in catalog.Objects)
            {
                token.ThrowIfCancellationRequested();
                var header = $"-- Read-only catalog snapshot: {Safe(catalog.ProfileId)} / {Safe(catalog.Database)}; {catalog.CapturedAt:O}\n";
                var metadataAnchor = header.Length + 3;
                header += $"-- {Safe(obj.Schema + "." + obj.Name)} ({obj.Kind})\n";
                var text = header + (obj.Definition ?? Describe(obj));
                var source = new SourceText(text);
                var parse = await new SqlParser(parserOptions with { QuotedIdentifier = obj.QuotedIdentifier },
                    new(DefaultDatabase: catalog.Database, IdentifierComparer: catalog.Comparer, DefaultSchema: catalog.DefaultSchema))
                    .ParseAsync(source, token);
                var path = Path.Combine(directory, obj.ObjectId + ".sql");
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), token);
                if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly);
                else File.SetUnixFileMode(path, UnixFileMode.UserRead);
                var uri = new Uri(path);
                var identity = new SqlObjectIdentity(catalog.ProfileId, null, catalog.Database, obj.Schema, obj.Name, obj.Kind);
                // Catalog identity remains authoritative after sp_rename; the header in stored text can be obsolete.
                var parsedDeclaration = parse.Declarations.FirstOrDefault(d => d.Kind == obj.Kind);
                var range = parsedDeclaration?.SelectionRange ?? source.Range(metadataAnchor, Safe(obj.Schema + "." + obj.Name).Length);
                var declaration = new SqlDeclaration(obj.Schema + "." + obj.Name, obj.Kind,
                    parsedDeclaration?.Range ?? range, range, new(null, catalog.Database, obj.Schema, obj.Name), catalog.Database,
                    Documentation: CatalogDocumentation.Describe(obj, catalog));
                var provenance = obj.Definition == null ? "Catalog metadata only; original source unavailable" : "Database module AST";
                symbols.Add(new(identity, declaration, new(uri, range, identity, SqlSourceKind.Database, provenance),
                    obj.Definition != null && parsedDeclaration != null ? SqlDefinitionFingerprint.Compute(parse, parsedDeclaration) : null,
                    catalog.CapturedAt));
                documents.Add(new(uri, null, SqlSourceKind.Database, parse));
            }
            generations.Enqueue(directory);
            // Keep one previous generation for recently returned locations; memory/file storage stays bounded.
            while (generations.Count > 2) DeleteOwned(generations.Dequeue());
            return new(catalog, documents.ToImmutable(), symbols.ToImmutable());
        }
        catch { DeleteOwned(directory); throw; }
    }

    private static string Describe(CatalogObject obj)
    {
        var text = new StringBuilder($"-- {Safe(obj.Schema + "." + obj.Name)} ({obj.Kind})\n-- Metadata description; this is not original DDL.\n");
        foreach (var column in obj.Columns)
            text.AppendLine($"-- Column {column.Ordinal}: {Safe(column.Name)} {Safe(column.TypeSchema)}.{Safe(column.Type)} length={column.MaxLength} precision={column.Precision} scale={column.Scale} nullable={column.Nullable}");
        foreach (var parameter in obj.Parameters)
            text.AppendLine($"-- Parameter {parameter.Ordinal}: {Safe(parameter.Name)} {Safe(parameter.TypeSchema)}.{Safe(parameter.Type)} length={parameter.MaxLength} precision={parameter.Precision} scale={parameter.Scale} output={parameter.Output}");
        return text.ToString();
    }

    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    private void DeleteOwned(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot cleanup path escaped its private root.");
        foreach (var file in Directory.EnumerateFiles(path)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var directory in generations) DeleteOwned(directory);
        Directory.Delete(Root);
    }
}
