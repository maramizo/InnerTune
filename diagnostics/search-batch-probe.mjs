import {searchBatch} from '../provider/search-batch.mjs';

let active = 0;
let maximumConcurrency = 0;
const started = Date.now();
const searches = await searchBatch(['first', 'second', 'third'], 1, async query => {
  active++;
  maximumConcurrency = Math.max(maximumConcurrency, active);
  await new Promise(resolve => setTimeout(resolve, 150));
  active--;
  if (query === 'second') throw new Error('synthetic failure');
  return [{id: `${query}-1`}, {id: `${query}-2`}];
});
const elapsedMilliseconds = Date.now() - started;

if (maximumConcurrency !== 3) throw new Error(`Expected three concurrent searches, observed ${maximumConcurrency}.`);
if (elapsedMilliseconds >= 350) throw new Error(`Searches appear sequential; elapsed ${elapsedMilliseconds}ms.`);
if (searches[0].tracks.length !== 1 || searches[2].tracks.length !== 1)
  throw new Error('The per-query result limit was not applied.');
if (searches[1].error !== 'synthetic failure' || searches[1].tracks.length !== 0)
  throw new Error('A failed query did not remain isolated from successful searches.');

console.log(JSON.stringify({
  passed: true,
  queryCount: searches.length,
  maximumConcurrency,
  elapsedMilliseconds,
  partialFailuresAreIsolated: true
}));
