using System.Runtime.CompilerServices;

// Lets Tests/App reach the App's internal types (for example CatalogItem,
// used by the tile type-ahead tests) without making them public API.
[assembly: InternalsVisibleTo("LoreFetch.Tests.App")]
