import {mkdir, readFile, rename, rm, stat, writeFile} from 'node:fs/promises';
import {dirname, join} from 'node:path';
import {homedir} from 'node:os';
import {randomUUID} from 'node:crypto';

const now = () => new Date().toISOString();
const empty = () => ({version: 1, volume: 72, shuffleEnabled: false, repeatMode: 'off', queueSourceId: null, queueSourceName: 'Current queue', playback: {status: 'idle', track: null, trackId: null, queueIndex: -1, queueId: null, queueName: 'Current queue', positionSeconds: 0, updatedAt: now()}, queue: [], folders: [], favorites: [], savedQueues: [], recentlyPlayed: [], settings: {theme: 'midnight', icon: 'dj-cat', customIconPath: null, animatedIconEnabled: true, autoResumeOnStart: false}, pendingCommands: [], updatedAt: now()});
export const libraryPath = () => join(process.env.ITMUSIC_DATA_DIR || join(homedir(), 'AppData', 'Local', 'InnerTune'), 'library.json');
const split = path => path.split(/[\\/]+/).map(x => x.trim()).filter(Boolean);

export class LibraryStore {
  constructor(path = libraryPath()) { this.path = path; this.lock = `${path}.lock`; }
  async read() {
    let data;
    try { data = {...empty(), ...JSON.parse(await readFile(this.path, 'utf8'))}; }
    catch (e) { if (e.code === 'ENOENT') data = empty(); else throw e; }
    try { data.playback = JSON.parse(await readFile(join(dirname(this.path), 'playback.json'), 'utf8')); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    return data;
  }
  async update(change) { return this.withLock(async () => { const data = await this.read(); await change(data); data.updatedAt = now(); const temp = `${this.path}.${process.pid}.tmp`; await mkdir(dirname(this.path), {recursive: true}); await writeFile(temp, `${JSON.stringify(data, null, 2)}\n`); await rename(temp, this.path); return data; }); }
  async withLock(action) { await mkdir(dirname(this.lock), {recursive: true}); const deadline = Date.now() + 5000; for (;;) { try { await mkdir(this.lock); break; } catch (e) { if (e.code !== 'EEXIST') throw e; try { if (Date.now() - (await stat(this.lock)).mtimeMs > 30000) await rm(this.lock, {recursive: true}); } catch {} if (Date.now() > deadline) throw new Error('Music library is busy.'); await new Promise(r => setTimeout(r, 40)); } } try { return await action(); } finally { await rm(this.lock, {recursive: true, force: true}); } }
  ensureFolder(data, path) { let parentId = null, folder; for (const name of split(path)) { folder = data.folders.find(x => x.parentId === parentId && x.name.toLowerCase() === name.toLowerCase()); if (!folder) { folder = {id: randomUUID(), name, parentId, createdAt: now()}; data.folders.push(folder); } parentId = folder.id; } return folder; }
  folderPath(data, id) { const parts = [], seen = new Set(); let item = data.folders.find(x => x.id === id); while (item && !seen.has(item.id)) { seen.add(item.id); parts.unshift(item.name); item = data.folders.find(x => x.id === item.parentId); } return parts.join('/'); }
  queuePath(data, queue) { return [this.folderPath(data, queue.folderId), queue.name].filter(Boolean).join('/'); }
  shortQueueId(id) { const compact = String(id || '').replace(/[^a-z0-9]/gi, ''); return compact.slice(0, 8); }
  findQueue(data, key) { const value = String(key).trim().replace(/^#/, '').toLowerCase(); const matches = data.savedQueues.filter(x => String(x.id).toLowerCase() === value || this.shortQueueId(x.id).toLowerCase() === value || x.name.toLowerCase() === value || this.queuePath(data, x).toLowerCase() === value); if (matches.length !== 1) throw new Error(matches.length ? 'Queue identifier is ambiguous; use its full ID or path.' : 'Saved queue not found.'); return matches[0]; }
}
