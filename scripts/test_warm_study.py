import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('warmstudy', Path(__file__).with_name('warm-sample-study.py'))
warm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(warm)
from warm_mcp_relay import WarmBackend, frame, send


class WarmStudyTests(unittest.TestCase):
    def test_client_isolation_and_exact_candidate_are_preserved(self):
        plan={'client':{'executable':'claude'},'model':'model','effort':'low','budget':.5,'candidate':{'name':'sql-test','path':'native-candidate'}}
        base=warm.client_command(plan,'grep')
        self.assertEqual({'mcpServers':{}},json.loads(base[base.index('--mcp-config')+1]))
        self.assertIn('--restricted',base);self.assertNotIn('--plugin-dir',base)
        plugin=warm.client_command(plan,'plugin',Path('private-endpoint'))
        self.assertEqual('native-candidate',plugin[plugin.index('--plugin-dir')+1])
        servers=json.loads(plugin[plugin.index('--mcp-config')+1])['mcpServers']
        self.assertEqual(['plugin:sql-test:sqlscriptdom-analysis'],list(servers))
        self.assertEqual(sys.executable,next(iter(servers.values()))['command'])
        self.assertNotIn('warmup',str(plugin))

    def test_warmup_is_separate_and_failed_usage_stays_unknown(self):
        plan={'model':'m','effort':'low','source_revision':'r','client':{'version':'v'},'candidate':{'version':'1'}}
        row=warm.row_for(plan,{'id':'q'},'plugin',1,1,2,120,1,[],0,{'elapsed_ms':80,'verified':False})
        self.assertEqual(120,row['elapsed_ms']);self.assertEqual(200,row['warmup_inclusive_ms'])
        self.assertIsNone(row['input_tokens']);self.assertIsNone(row['reported_cost_usd'])
        self.assertFalse(row['warm_backend_verified'])

    def test_relay_forwards_new_calls_to_the_same_preinitialized_process(self):
        # Actual child process with a counter: no production result cache can fake this assertion.
        source='''import json,sys
count=0
for line in sys.stdin:
 m=json.loads(line)
 if 'id' not in m:continue
 if m['method']=='server/discover':
  print(json.dumps({'jsonrpc':'2.0','id':m['id'],'error':{'code':-32601,'message':'Method not found'}}),flush=True);continue
 if m['method']=='initialize':r={'protocolVersion':'2025-11-25','serverInfo':{'name':'fake'},'capabilities':{}}
 else:
  count+=1
  r={'structuredContent':{'indexedDocuments':1,'counter':count,'timings':{'coldStart':count==1}}}
 print(json.dumps({'jsonrpc':'2.0','id':m['id'],'result':r}),flush=True)
'''
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);script=root/'server.py';script.write_text(source,encoding='utf-8')
            with WarmBackend([sys.executable,str(script)],root,1) as backend:
                import socket
                config=json.loads(backend.endpoint.read_text())
                with socket.create_connection(('127.0.0.1',config['port']),timeout=5) as connection:
                    stream=connection.makefile('rwb',buffering=0)
                    send(stream,{'key':config['key']});self.assertEqual({'relayReady':True},frame(stream))
                    send(stream,{'id':0,'method':'server/discover','params':{'protocolVersion':'2026-07-28'}})
                    self.assertEqual(-32601,frame(stream)['error']['code'])
                    send(stream,{'id':1,'method':'initialize','params':{'protocolVersion':'2026-07-28'}})
                    self.assertEqual('fake',frame(stream)['result']['serverInfo']['name'])
                    send(stream,{'id':2,'method':'tools/call','params':{'name':'find_procedures'}})
                    reply=frame(stream)['result']['structuredContent']
                    self.assertEqual(3,reply['counter']);self.assertFalse(reply['timings']['coldStart'])
                    evidence=backend.evidence()
                    self.assertTrue(evidence['same_backend_alive_after_client'])
                    self.assertEqual(backend.pid,evidence['measured_calls'][0]['backend_pid'])
                    self.assertEqual(['server/discover'],evidence['preinitialize_methods'])
                    self.assertEqual('2026-07-28',evidence['client_requested_protocol_version'])
                    stream.close()
            self.assertFalse(backend.endpoint.exists());self.assertIsNotNone(backend.process.poll())

    def test_protocol_framing_has_a_bound(self):
        value={'text':'a\nb'};stream=io.BytesIO();send(stream,value);stream.seek(0);self.assertEqual(value,frame(stream))
        with self.assertRaises(ValueError):frame(io.BytesIO(b'x'*(8*1024*1024+2)))


if __name__=='__main__':unittest.main()
