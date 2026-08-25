#!/usr/bin/env node
import {createInterface} from 'node:readline';
import {join} from 'node:path';
import {homedir} from 'node:os';
import {mkdir, readFile, rename, rm, writeFile} from 'node:fs/promises';
import {dirname} from 'node:path';
import {Readable} from 'node:stream';
import {pipeline} from 'node:stream/promises';
import {createWriteStream} from 'node:fs';
import {BotGuardClient, getChallenge} from 'bgutils-js/botguard';
import {buildURL, getHeaders, USER_AGENT} from 'bgutils-js/utils';
import {WebPoMinter} from 'bgutils-js/webpo';
import {JSDOM} from 'jsdom';
import {Innertube, Platform, UniversalCache} from 'youtubei.js';

Platform.shim.eval = async (data) => new Function(data.output)();
let client;
let minterPromise;
let dataDirectory;

async function initialize() {
  if (client) return;
  dataDirectory = process.env.ITMUSIC_DATA_DIR || join(homedir(), 'AppData', 'Local', 'InnerTune');
  client = await Innertube.create({cache: new UniversalCache(true, join(dataDirectory, 'provider-cache')), lang: 'en', location: 'US', enable_session_cache: true});
}

function thumbnail(item) {
  const list = item?.thumbnails || item?.thumbnail?.contents || item?.thumbnail || [];
  const url = Array.isArray(list) ? list.at(-1)?.url : undefined;
  return url?.replace(/=w\d+-h\d+(?:-l\d+)?-rj$/, '=w226-h226-l90-rj');
}

function videoThumbnail(item) {
  const list = item?.thumbnails || item?.thumbnail?.contents || item?.thumbnail || [];
  return Array.isArray(list) ? list.at(-1)?.url : undefined;
}

function artistId(person) {
  return person?.channel_id || person?.id || person?.endpoint?.payload?.browseId;
}

async function search(query) {
  await initialize();
  const response = await client.music.search(query, {type: 'song'});
  return (response.songs?.contents || []).filter(item => item.id && item.title).slice(0, 30).map(item => ({
    id: item.id,
    title: item.title,
    artist: item.artists?.map(artist => artist.name).join(', ') || item.author?.name || 'Unknown artist',
    artistId: artistId(item.artists?.[0]) || artistId(item.author),
    album: item.album?.name,
    durationSeconds: item.duration?.seconds || 0,
    durationText: item.duration?.text || '--:--',
    artworkUrl: thumbnail(item)
  }));
}

function text(value) {
  if (typeof value === 'string') return value;
  return value?.toString?.() || value?.text || '';
}

function trackFromItem(item, artworkFallback, artistFallback, albumFallback) {
  const id = item.id || item.endpoint?.payload?.videoId;
  const title = text(item.title) || item.name;
  if (!id || !title) return undefined;
  const artists = item.artists?.map(artist => artist.name).filter(Boolean) || [];
  const artist = artists.join(', ') || item.author?.name || item.authors?.map(author => author.name).join(', ') || artistFallback || 'Unknown artist';
  const durationSeconds = item.duration?.seconds || 0;
  return {
    id,
    title,
    artist,
    artistId: artistId(item.artists?.[0]) || artistId(item.author) || artistId(item.authors?.[0]),
    album: item.album?.name || albumFallback,
    durationSeconds,
    durationText: item.duration?.text || (durationSeconds ? formatDuration(durationSeconds) : '--:--'),
    artworkUrl: thumbnail(item) || artworkFallback
  };
}

function discoveryItem(item) {
  const itemType = item.item_type || '';
  const id = item.id || item.endpoint?.payload?.browseId || item.endpoint?.payload?.videoId;
  const title = text(item.title) || item.name || item.button_text;
  if (!id || !title || itemType === 'artist') return undefined;
  const kind = itemType === 'playlist' ? 'playlist' : itemType === 'album' ? 'album' : itemType === 'song' || itemType === 'video' ? 'song' : undefined;
  if (!kind) return undefined;
  const subtitle = text(item.subtitle) || item.artists?.map(artist => artist.name).join(', ') || item.author?.name || '';
  return {
    id,
    kind,
    title,
    subtitle,
    artworkUrl: thumbnail(item),
    track: kind === 'song' ? trackFromItem(item) : undefined
  };
}

function sectionFromShelf(shelf, titleOverride, limit = 16) {
  const items = (shelf?.contents || []).map(discoveryItem).filter(Boolean);
  if (!items.length) return undefined;
  return {title: titleOverride || text(shelf.header?.title) || 'For you', items: uniqueItems(items).slice(0, limit)};
}

function uniqueItems(items) {
  const seen = new Set();
  return items.filter(item => item.id && !seen.has(item.id) && seen.add(item.id));
}

async function home(argument = {}) {
  await initialize();
  const seedVideoId = argument?.seedVideoId || '';
  const legacyKeys = [...new Set([seedVideoId && `home-${seedVideoId}`, 'home-generic'].filter(Boolean))];
  return withDiscoveryCache('home', 45 * 60 * 1000, !!argument?.refresh, async () => {
    const [homeFeed, explore, related] = await Promise.all([
      client.music.getHomeFeed(),
      client.music.getExplore(),
      seedVideoId ? client.music.getRelated(seedVideoId).catch(() => undefined) : Promise.resolve(undefined)
    ]);
    const sections = [];
    if (related?.contents) {
      const relatedSongs = [...related.contents].find(shelf => /you might also like|similar/i.test(text(shelf.header?.title)));
      const quickPicks = sectionFromShelf(relatedSongs, 'Quick picks', 12);
      if (quickPicks) sections.push(quickPicks);
    }
    const newReleases = [...(explore.sections || [])].find(shelf => /new albums|new releases/i.test(text(shelf.header?.title)));
    const trending = [...(explore.sections || [])].find(shelf => /^trending$/i.test(text(shelf.header?.title)));
    const releaseSection = sectionFromShelf(newReleases, 'New albums & singles', 12);
    const trendingSection = sectionFromShelf(trending, 'Trending now', 12);
    if (releaseSection) sections.push(releaseSection);
    if (trendingSection) sections.push(trendingSection);
    for (const shelf of homeFeed.sections || []) {
      const curated = sectionFromShelf(shelf, undefined, 12);
      if (curated && curated.items.some(item => item.kind === 'playlist')) sections.push(curated);
      if (sections.length >= 6) break;
    }
    return {
      sections,
      moods: (homeFeed.filters || []).filter(filter => filter !== 'Podcasts'),
      fetchedAt: new Date().toISOString()
    };
  }, legacyKeys);
}

async function mood(argument = {}) {
  await initialize();
  const name = String(argument?.name || '').trim();
  if (!name) throw new Error('Choose a mood first.');
  const key = `mood-${name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
  return withDiscoveryCache(key, 2 * 60 * 60 * 1000, !!argument?.refresh, async () => {
    const homeFeed = await client.music.getHomeFeed();
    const filtered = await homeFeed.applyFilter(name);
    return {
      sections: (filtered.sections || []).map(shelf => sectionFromShelf(shelf)).filter(Boolean).slice(0, 6),
      moods: (homeFeed.filters || []).filter(filter => filter !== 'Podcasts'),
      fetchedAt: new Date().toISOString()
    };
  });
}

async function collection(argument = {}) {
  await initialize();
  const id = String(argument?.id || '');
  const kind = argument?.kind === 'album' ? 'album' : 'playlist';
  if (!id) throw new Error('This collection has no id.');
  if (kind === 'album') {
    const album = await client.music.getAlbum(id);
    const artworkUrl = thumbnail(album.header) || thumbnail(album.background);
    const albumTitle = text(album.header?.title) || 'Album';
    const artist = album.header?.author?.name || text(album.header?.strapline_text_one) || text(album.header?.subtitle).split('•').map(part => part.trim()).find(part => part && !/^album$|^single$|^ep$|^\d{4}$/i.test(part));
    return {
      id, kind,
      title: albumTitle,
      subtitle: text(album.header?.subtitle) || text(album.header?.description),
      artworkUrl,
      tracks: (album.contents || []).map(item => trackFromItem(item, artworkUrl, artist, albumTitle)).filter(Boolean)
    };
  }
  const playlist = await client.music.getPlaylist(id);
  const artworkUrl = thumbnail(playlist.header) || thumbnail(playlist.background);
  return {
    id, kind,
    title: text(playlist.header?.title) || 'Playlist',
    subtitle: text(playlist.header?.subtitle) || text(playlist.header?.description),
    artworkUrl,
    tracks: (playlist.items || []).map(item => trackFromItem(item, artworkUrl)).filter(Boolean)
  };
}

function artistTrackItems(value, depth = 0, found = []) {
  if (!value || depth > 5) return found;
  if (Array.isArray(value)) {
    for (const item of value) artistTrackItems(item, depth + 1, found);
    return found;
  }
  if (typeof value !== 'object') return found;
  const type = String(value.item_type || '').toLowerCase();
  if ((type === 'song' || type === 'video' || value.duration?.seconds) && (value.id || value.endpoint?.payload?.videoId))
    found.push(value);
  for (const key of ['sections', 'contents', 'items', 'songs', 'results'])
    if (value[key]) artistTrackItems(value[key], depth + 1, found);
  return found;
}

function sameArtist(trackArtist, requested) {
  const wanted = cleanArtistName(requested);
  const actual = cleanArtistName(trackArtist);
  return wanted && (actual === wanted || actual.startsWith(`${wanted} `) || actual.endsWith(` ${wanted}`));
}

function cleanArtistName(value) {
  return String(value || '').toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, ' ').trim();
}

async function resolveArtist(requestedName) {
  if (!requestedName) return undefined;
  const response = await client.music.search(requestedName, {type: 'artist'});
  const candidates = (response.artists?.contents || [])
    .map(item => ({
      id: artistId(item) || item.endpoint?.payload?.browseId,
      name: item.name || text(item.title),
      artworkUrl: thumbnail(item)
    }))
    .filter(candidate => candidate.id?.startsWith('UC') && candidate.name);
  const wanted = cleanArtistName(requestedName);
  return candidates.find(candidate => cleanArtistName(candidate.name) === wanted);
}

function continuationItems(response) {
  return [
    ...(response?.continuation_contents?.contents || []),
    ...(response?.on_response_received_actions || []).flatMap(action => action.contents || [])
  ];
}

async function allArtistSongItems(shelf) {
  const songs = [];
  const seenContinuations = new Set();
  let contents = [...(shelf?.contents || [])];
  let continuationToken = shelf?.continuation;
  for (let page = 0; page < 50 && contents.length && songs.length < 10_000; page++) {
    songs.push(...contents.filter(item => item?.type !== 'ContinuationItem'));
    const continuationItem = contents.find(item => item?.type === 'ContinuationItem' && item.endpoint);
    const token = continuationItem?.endpoint?.payload?.token || continuationToken;
    if (!token || seenContinuations.has(token)) break;
    seenContinuations.add(token);
    const response = continuationItem
      ? await continuationItem.endpoint.call(client.actions, {client: 'YTMUSIC', parse: true})
      : await client.actions.execute('/browse', {continuation: token, client: 'YTMUSIC', parse: true});
    contents = continuationItems(response);
    continuationToken = response?.continuation_contents?.continuation;
  }
  return songs;
}

async function artist(argument = {}) {
  await initialize();
  const suppliedId = String(argument?.id || '').trim();
  const requestedName = String(argument?.name || '').trim();
  if (!requestedName && !suppliedId) throw new Error('This artist has no name or id.');
  let id = suppliedId;
  let resolved;
  let page = id ? await client.music.getArtist(id).catch(() => undefined) : undefined;
  if ((!page?.header || !page.sections?.length) && requestedName) {
    resolved = await resolveArtist(requestedName).catch(() => undefined);
    id = resolved?.id || '';
    page = id ? await client.music.getArtist(id).catch(() => undefined) : undefined;
  }
  let pageSongs;
  if (page && typeof page.getAllSongs === 'function') {
    try { pageSongs = await page.getAllSongs(); } catch {}
  }
  const header = page?.header;
  const name = text(header?.title) || resolved?.name || requestedName || 'Artist';
  const artworkUrl = thumbnail(header) || thumbnail(page?.background) || resolved?.artworkUrl;
  const catalogItems = pageSongs ? await allArtistSongItems(pageSongs) : artistTrackItems(page);
  const catalogTracks = catalogItems
    .map(item => trackFromItem(item, artworkUrl, name))
    .filter(Boolean);
  const searchedTracks = catalogTracks.length || !requestedName
    ? []
    : (await search(`${requestedName} songs`)).filter(track => sameArtist(track.artist, requestedName || name));
  const tracks = uniqueItems(catalogTracks.length ? catalogTracks : searchedTracks);
  return {
    id: id || `artist:${name}`,
    kind: 'artist',
    title: name,
    subtitle: `${tracks.length} ${tracks.length === 1 ? 'song' : 'songs'}`,
    artworkUrl: artworkUrl || tracks[0]?.artworkUrl,
    tracks
  };
}

async function withDiscoveryCache(key, maxAgeMilliseconds, refresh, producer, fallbackKeys = []) {
  const directory = join(dataDirectory, 'discovery-cache');
  const path = join(directory, `${key}.json`);
  let cached;
  for (const candidate of [key, ...fallbackKeys]) {
    try {
      cached = JSON.parse(await readFile(join(directory, `${candidate}.json`), 'utf8'));
      if (cached) break;
    } catch {}
  }
  const age = cached?.fetchedAt ? Date.now() - new Date(cached.fetchedAt).getTime() : Number.POSITIVE_INFINITY;
  if (!refresh && cached && age <= maxAgeMilliseconds) return cached;
  try {
    const result = await producer();
    await mkdir(directory, {recursive: true});
    await writeFile(`${path}.tmp`, JSON.stringify(result), 'utf8');
    await rename(`${path}.tmp`, path);
    return result;
  } catch (error) {
    if (cached) return {...cached, stale: true};
    throw error;
  }
}

function formatDuration(seconds) {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;
}

async function resolve(videoId) {
  const {info, poToken} = await playableInfo(videoId);
  const formats = info.streaming_data?.adaptive_formats || [];
  const aac = formats.filter(format => format.has_audio && !format.has_video && format.mime_type?.includes('audio/mp4')).sort((a, b) => (b.bitrate || 0) - (a.bitrate || 0))[0];
  const format = aac || info.chooseFormat({type: 'audio', quality: 'best', language: 'original'});
  const url = new URL(await format.decipher(client.session.player));
  url.searchParams.set('pot', poToken);
  return {url: url.toString()};
}

async function video(videoId) {
  const {info, poToken} = await playableInfo(videoId);
  const formats = (info.streaming_data?.formats || [])
    .filter(format => format.has_audio && format.has_video && format.mime_type?.includes('video/mp4'));
  const preferred = formats.filter(format => (format.height || 0) <= 480).sort((a, b) => (b.height || 0) - (a.height || 0) || (b.bitrate || 0) - (a.bitrate || 0))[0];
  const format = preferred || formats.sort((a, b) => (a.height || Number.MAX_SAFE_INTEGER) - (b.height || Number.MAX_SAFE_INTEGER))[0];
  if (!format) throw new Error('A Windows-compatible video stream is not available for this song.');
  const url = new URL(await format.decipher(client.session.player));
  url.searchParams.set('pot', poToken);
  return {url: url.toString(), quality: format.quality_label || `${format.height || 360}p`};
}

function normalizedWords(value) {
  return String(value || '').toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, ' ').trim().split(/\s+/).filter(word => word.length > 1);
}

function overlapScore(left, right) {
  const a = new Set(normalizedWords(left));
  const b = new Set(normalizedWords(right));
  if (!a.size || !b.size) return 0;
  let matches = 0;
  for (const word of a) if (b.has(word)) matches++;
  return matches / Math.max(1, a.size);
}

function cleanTrackTitle(value) {
  return String(value || '')
    .replace(/[([][^\])]*(?:remaster(?:ed)?|album version|single version|radio edit|explicit|clean)[^\])]*[\])]/gi, ' ')
    .replace(/\b(?:19|20)\d{2}\s+remaster(?:ed)?\b/gi, ' ')
    .replace(/\s+-\s+remaster(?:ed)?.*$/i, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function isOfficialVideo(value) {
  return /\bofficial\b[^|\n]{0,24}\b(?:music\s+)?video\b/i.test(value) || /\bvevo\b/i.test(value);
}

function videoKind(title, author) {
  const value = `${title} ${author}`.toLowerCase();
  if (/\b(live|concert|performance|session)\b/.test(value)) return 'Live';
  if (/\b(lyric|lyrics)\b/.test(value)) return 'Lyrics';
  if (/\bvisuali[sz]er\b/.test(value)) return 'Visualizer';
  if (isOfficialVideo(value)) return 'Official video';
  if (/\bmusic\s+video\b/.test(value)) return 'Music video';
  return 'Video';
}

function scoreVideoCandidate(item, track) {
  const title = text(item.title);
  const author = item.author?.name || text(item.author) || text(item.byline_text);
  const searchable = `${title} ${author}`.toLowerCase();
  const durationSeconds = item.duration?.seconds || 0;
  const durationDelta = durationSeconds && track.durationSeconds ? Math.abs(durationSeconds - track.durationSeconds) : 999;
  const artistOverlap = overlapScore(track.artist, author);
  const titleOverlap = overlapScore(cleanTrackTitle(track.title), title);
  let score = Math.round(titleOverlap * 42 + artistOverlap * 30);
  if (titleOverlap < 0.5) score -= 45;
  if (isOfficialVideo(searchable)) score += 40;
  if (/\bvevo\b/.test(searchable) || item.author?.is_verified) score += 15;
  if (isOfficialVideo(title) && artistOverlap < 0.5 && !item.author?.is_verified && !/\bvevo\b/.test(author)) score -= 20;
  if (durationDelta <= 3) score += 20;
  else if (durationDelta <= 8) score += 14;
  else if (durationDelta <= 20) score += 5;
  else if (durationDelta > 45) score -= 20;
  if (/\b(official\s+audio|audio\s+only|provided\s+to\s+youtube)\b/.test(searchable)) score -= 55;
  if (/\b(remaster(?:ed)?|album version)\b/.test(searchable) && !isOfficialVideo(searchable) && !/\bmusic\s+video\b/.test(searchable)) score -= 30;
  if (/\btopic\b/.test(author.toLowerCase()) || /\b(static|album art|full album|reaction|cover)\b/.test(searchable)) score -= 28;
  if (/\b(lyrics?|visuali[sz]er)\b/.test(searchable)) score -= 12;
  if (/\b(instrumental|karaoke|clean)\b/.test(searchable) && !/\b(instrumental|karaoke|clean)\b/i.test(track.title)) score -= 28;
  if (/\b(remix|sped up|slowed|nightcore)\b/.test(searchable) && !/\b(remix|sped up|slowed|nightcore)\b/i.test(track.title)) score -= 22;
  const kind = videoKind(title, author);
  if (kind === 'Video' && artistOverlap >= 0.7 && durationDelta <= 3) score -= 25;
  if (kind === 'Video' && !item.author?.is_verified && !/\bvevo\b/.test(searchable)) score -= 10;
  if (kind === 'Live') score -= 25;
  return {score: Math.max(0, Math.min(100, score)), kind, durationDelta};
}

async function videoCandidates(argument = {}) {
  await initialize();
  const input = argument.track || argument.Track || argument;
  const track = {
    id: input.id || input.Id || '',
    title: input.title || input.Title || '',
    artist: input.artist || input.Artist || '',
    durationSeconds: input.durationSeconds ?? input.DurationSeconds ?? 0
  };
  if (!track?.title || !track?.artist) throw new Error('A song title and artist are required to find videos.');
  const customQuery = String(argument.query || '').trim();
  const cleanTitle = cleanTrackTitle(track.title);
  const queries = customQuery ? [customQuery] : [
    `${track.artist} - ${cleanTitle} (Official Video)`,
    `"${cleanTitle}" ${track.artist} official music video`
  ];
  const responses = [];
  for (const query of queries) responses.push(await client.search(query, {type: 'video'}));
  const candidates = [];
  const seen = new Set();
  for (const item of responses.flatMap(response => response.results || [])) {
    const id = item.video_id || item.id;
    const title = text(item.title);
    if (!id || !title || seen.has(id) || item.is_live || item.is_upcoming) continue;
    seen.add(id);
    const author = item.author?.name || text(item.author) || text(item.byline_text) || 'YouTube';
    const durationSeconds = item.duration?.seconds || 0;
    const scored = scoreVideoCandidate(item, track);
    candidates.push({
      id,
      title,
      author,
      thumbnailUrl: videoThumbnail(item),
      durationSeconds,
      durationText: item.duration?.text || (durationSeconds ? formatDuration(durationSeconds) : '--:--'),
      score: scored.score,
      kind: scored.kind,
      useVideoAudio: scored.durationDelta > 3 || scored.kind === 'Live'
    });
  }
  candidates.sort((a, b) => b.score - a.score || Math.abs((a.durationSeconds || 0) - (track.durationSeconds || 0)) - Math.abs((b.durationSeconds || 0) - (track.durationSeconds || 0)));
  return candidates.slice(0, 8).map((candidate, index) => ({...candidate, recommended: index === 0 && candidate.score >= 55}));
}

async function playableInfo(videoId) {
  await initialize();
  const minter = await getPoMinter();
  const poToken = await minter.mintAsWebsafeString(videoId);
  const info = await client.getBasicInfo(videoId, {client: 'YTMUSIC', po_token: poToken});
  if (info.playability_status?.status !== 'OK') throw new Error(info.playability_status?.reason || 'This track is not playable.');
  return {info, poToken};
}

async function download({videoId, destination}) {
  const {url} = await resolve(videoId);
  const response = await fetch(url);
  if (!response.ok || !response.body) throw new Error(`Audio download failed (${response.status}).`);
  await mkdir(dirname(destination), {recursive: true});
  const temporary = `${destination}.download`;
  try {
    await pipeline(Readable.fromWeb(response.body), createWriteStream(temporary));
    await rename(temporary, destination);
  } catch (error) {
    await rm(temporary, {force: true});
    throw error;
  }
  return {path: destination};
}

function getPoMinter() {
  minterPromise ||= createPoMinter();
  return minterPromise;
}

async function createPoMinter() {
  const requestKey = 'O43z0dpjhgX20SCx4KAo';
  const dom = new JSDOM('<!doctype html><html><body></body></html>', {url: 'https://www.youtube.com/', referrer: 'https://www.youtube.com/'});
  Object.assign(globalThis, {window: dom.window, document: dom.window.document, location: dom.window.location, origin: dom.window.origin});
  if (!Reflect.has(globalThis, 'navigator')) Object.defineProperty(globalThis, 'navigator', {value: dom.window.navigator});
  const challenge = await getChallenge({fetchFunction: fetch, requestKey});
  const interpreter = challenge.interpreterJavascript?.privateDoNotAccessOrElseSafeScriptWrappedValue;
  if (!interpreter) throw new Error('YouTube did not return a playback challenge.');
  new Function(interpreter)();
  const botGuard = await BotGuardClient.create({program: challenge.program, globalName: challenge.globalName, globalObject: globalThis});
  const webPoSignalOutput = [];
  const botguardResponse = await botGuard.snapshot({webPoSignalOutput});
  const response = await fetch(buildURL('GenerateIT', true), {method: 'POST', headers: {...getHeaders(), 'user-agent': USER_AGENT}, body: JSON.stringify([requestKey, botguardResponse])});
  if (!response.ok) throw new Error(`YouTube playback attestation failed (${response.status}).`);
  const [integrityToken, estimatedTtlSecs, mintRefreshThreshold, websafeFallbackToken] = await response.json();
  return WebPoMinter.create({integrityToken, estimatedTtlSecs, mintRefreshThreshold, websafeFallbackToken}, webPoSignalOutput);
}

async function run(command, argument) {
  if (command === 'search') return search(argument);
  if (command === 'home') return home(argument);
  if (command === 'mood') return mood(argument);
  if (command === 'collection') return collection(argument);
  if (command === 'artist') return artist(argument);
  if (command === 'resolve') return resolve(argument);
  if (command === 'video') return video(argument);
  if (command === 'video_candidates') return videoCandidates(argument);
  if (command === 'download') return download(argument);
  throw new Error(`Unknown provider command: ${command}`);
}

if (process.argv[2] === 'serve') {
  const input = createInterface({input: process.stdin});
  for await (const line of input) {
    try {
      const request = JSON.parse(line);
      const result = await run(request.command, request.argument);
      process.stdout.write(`${JSON.stringify({id: request.id, result})}\n`);
    } catch (error) {
      let id;
      try { id = JSON.parse(line).id; } catch {}
      process.stdout.write(`${JSON.stringify({id, error: error instanceof Error ? error.message : String(error)})}\n`);
    }
  }
} else {
  try {
    const argument = ['download', 'home', 'mood', 'collection', 'artist', 'video_candidates'].includes(process.argv[2]) ? JSON.parse(process.argv[3]) : process.argv[3];
    process.stdout.write(JSON.stringify(await run(process.argv[2], argument)));
  }
  catch (error) { process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`); process.exitCode = 1; }
}
