using System.Text.Json;
using Docovee.BLL.Models;
using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public static class SearchContextHelper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SearchContextData Load(SearchSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SearchContextJson))
            return new SearchContextData();

        try
        {
            return JsonSerializer.Deserialize<SearchContextData>(session.SearchContextJson, Options) ?? new SearchContextData();
        }
        catch
        {
            return new SearchContextData();
        }
    }

    public static void Save(SearchSession session, SearchContextData context)
    {
        session.SearchContextJson = JsonSerializer.Serialize(context, Options);
    }
}
