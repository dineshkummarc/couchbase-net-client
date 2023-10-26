using Couchbase.Search;

namespace Couchbase.Stellar.CouchbaseClient;

public class ProtoSearchQueryRow : ISearchQueryRow
{
    public string Id { get; internal set; }
    public double Score { get; internal set; }
    public string Index { get; internal set; }
    public dynamic Explanation { get; internal set; }
    public dynamic Locations { get; internal set; }
    public IDictionary<string, dynamic> Fields { get; internal set; }
    public IDictionary<string, List<string>> Fragments { get; internal set; }
}