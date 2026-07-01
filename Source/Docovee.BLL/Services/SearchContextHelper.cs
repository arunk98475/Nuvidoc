using System.Text.Json;
using Docovee.DS.Entities;
using Docovee.DS.Models;

namespace Docovee.BLL.Services;

public static class SearchContextHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SearchContextData Load(SearchSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SearchContextJson))
            return new SearchContextData();

        try
        {
            return JsonSerializer.Deserialize<SearchContextData>(session.SearchContextJson, JsonOptions) ?? new SearchContextData();
        }
        catch
        {
            return new SearchContextData();
        }
    }

    public static void Save(SearchSession session, SearchContextData context)
    {
        session.SearchContextJson = JsonSerializer.Serialize(context, JsonOptions);
    }
}
