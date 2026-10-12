using Xunit;

// Each class launches a real server process; bound parallelism on developer/CI hosts.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
