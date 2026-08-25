import {mkdtemp, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {Client} from '../provider/node_modules/@modelcontextprotocol/sdk/dist/esm/client/index.js';
import {StdioClientTransport} from '../provider/node_modules/@modelcontextprotocol/sdk/dist/esm/client/stdio.js';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const dataDirectory = await mkdtemp(join(tmpdir(), 'InnerTuneMcpSearch-'));
const client = new Client({name: 'innertune-search-probe', version: '1.0.0'});
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [join(root, 'provider', 'mcp-server.mjs')],
  env: {...process.env, ITMUSIC_DATA_DIR: dataDirectory}
});

try {
  await client.connect(transport);
  const result = await client.callTool({
    name: 'search_songs',
    arguments: {query: ['Daft Punk One More Time', 'Justice D.A.N.C.E.'], limit: 2}
  });
  const payload = JSON.parse(result.content.find(item => item.type === 'text')?.text || '{}');
  if (payload.searches?.length !== 2) throw new Error('The MCP tool did not return one result group per query.');
  if (payload.searches.some(search => search.tracks.length === 0 || search.tracks.length > 2))
    throw new Error('A parallel MCP search returned an invalid number of tracks.');
  console.log(JSON.stringify({passed: true, queryArrayAccepted: true, resultGroups: payload.searches.length}));
}
finally {
  await client.close().catch(() => {});
  await rm(dataDirectory, {recursive: true, force: true});
}
