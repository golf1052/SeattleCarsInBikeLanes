using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BikeLanesController : ControllerBase
    {
        private const string ExistingBikeLaneFacilitiesUrl = "https://gisrevprxy.seattle.gov/arcgis/rest/services/SDOT_EXT/BikeMap/MapServer/20/query";
        private const string MultiUseTrailsUrl = "https://gisrevprxy.seattle.gov/arcgis/rest/services/SDOT_EXT/BikeMap/MapServer/19/query";

        private readonly HttpClient httpClient;
        private readonly IMemoryCache cache;

        public BikeLanesController(HttpClient httpClient,
            IMemoryCache cache)
        {
            this.httpClient = httpClient;
            this.cache = cache;
        }

        [HttpGet]
        public async Task<string> GetBikeLanes([FromQuery] string type)
        {
            if (type == "bikelanes")
            {
                var cachedValue = await cache.GetOrCreateAsync(type, async cacheEntry =>
                {
                    cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                    return (await GetAllGeometry(ExistingBikeLaneFacilitiesUrl)).ToString(Newtonsoft.Json.Formatting.None);
                });
                return cachedValue!;
            }
            else if (type == "trails")
            {
                var cachedValue = await cache.GetOrCreateAsync(type, async cacheEntry =>
                {
                    cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                    return (await GetAllGeometry(MultiUseTrailsUrl)).ToString(Newtonsoft.Json.Formatting.None);
                });
                return cachedValue!;
            }
            else
            {
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return "Only \"bikelanes\" and \"trails\" supported";
            }
        }

        private async Task<JArray> GetAllGeometry(string baseUrl)
        {
            JArray allFeatures = new JArray();
            List<int> objectIds = await GetObjectIds(baseUrl);
            for (int i = 0; i < objectIds.Count; i += 250)
            {
                List<int> section = objectIds.GetRange(i, Math.Min(250, objectIds.Count - i));
                JObject featureCollection = await GetGeometry(baseUrl, section);
                foreach (JObject feature in (JArray)featureCollection["features"]!)
                {
                    allFeatures.Add(feature);
                }
            }
            return allFeatures;
        }

        private async Task<List<int>> GetObjectIds(string baseUrl)
        {
            HttpResponseMessage response = await httpClient.GetAsync($"{baseUrl}?where=1%3D1&returnIdsOnly=true&f=geojson");
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error when fetching object ids for {baseUrl}");
            }

            JObject responseObject = JObject.Parse(await response.Content.ReadAsStringAsync());
            return responseObject["objectIds"]!.ToObject<List<int>>()!;
        }

        private async Task<JObject> GetGeometry(string baseUrl, List<int> objectIds)
        {
            if (objectIds.Count > 1000)
            {
                throw new Exception("Cannot request more than 1000 object ids at a time.");
            }

            string outFields = string.Empty;
            if (baseUrl.Contains("20"))
            {
                // Bike lanes
                outFields = "OBJECTID,UNITID,CATEGORY";
            }
            else if (baseUrl.Contains("19"))
            {
                // Trails
                outFields = "OBJECTID,ORD_STNAME_CONCAT";
            }

            Uri uri = new Uri($"{baseUrl}?where=1%3D1&objectIds={string.Join(',', objectIds)}&outFields={outFields}&f=geojson");
            HttpResponseMessage response = await httpClient.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Error when fetching geometry for {baseUrl}");
            }

            JObject responseObject = JObject.Parse(await response.Content.ReadAsStringAsync());
            return responseObject;
        }
    }
}
