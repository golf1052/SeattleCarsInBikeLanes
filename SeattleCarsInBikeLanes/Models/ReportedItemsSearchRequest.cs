using Microsoft.Azure.Cosmos.Spatial;

namespace SeattleCarsInBikeLanes.Models
{
    public class ReportedItemsSearchRequest
    {
        public int? MinCars { get; set; }
        public int? MaxCars { get; set; }
        public DateOnly? MinDate { get; set; }
        public DateOnly? MaxDate { get; set; }
        public TimeOnly? MinTime { get; set; }
        public TimeOnly? MaxTime { get; set; }
        public Position? Location { get; set; }
        public double? DistanceFromLocationInMiles { get; set; }
    }
}
