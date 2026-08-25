export async function searchBatch(queries, limit, searchOne) {
  return Promise.all(queries.map(async query => {
    try {
      const tracks = (await searchOne(query)).slice(0, limit);
      return {query, tracks};
    }
    catch (error) {
      return {query, tracks: [], error: error instanceof Error ? error.message : String(error)};
    }
  }));
}
