using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Database
{
    public class MastodonOAuthMappingDatabase : AbstractDatabase<MastodonOAuthMapping>
    {
        public MastodonOAuthMappingDatabase(ILogger<MastodonOAuthMappingDatabase> logger, Container mappingContainer) :
            base(logger, mappingContainer)
        {
        }
    }
}
