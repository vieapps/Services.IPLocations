#region Related components
using System;
using System.Diagnostics;

using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;

using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	[Serializable, BsonIgnoreExtraElements, DebuggerDisplay("IP = {IP}, AppInfo = {AppInfo}")]
	[Entity(CollectionName = "IPLocations", TableName = "T_IPLocations_Info", CacheClass = typeof(Utility), CacheName = "Cache", CreateNewVersionWhenUpdated = false)]
	public class IPLocation : Repository<IPLocation>
	{
		public IPLocation() => this.ID = "";

		#region Properties
		/// <summary>
		/// Gets or sets the IP address
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(UniqueIndexName = "IP_Address")]
		public string IP { get; set; } = "";

		/// <summary>
		/// Gets or sets the city name
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable]
		public string City { get; set; } = "";

		/// <summary>
		/// Gets or sets the region name
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable]
		public string Region { get; set; } = "";

		/// <summary>
		/// Gets or sets the country
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable]
		public string Country { get; set; } = "";

		/// <summary>
		/// Gets or sets the country code
		/// </summary>
		[Property(MaxLength = 2, NotNull = true), Sortable]
		public string CountryCode { get; set; } = "";

		/// <summary>
		/// Gets or sets the continent name (earth area)
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable]
		public string Continent { get; set; } = "";

		/// <summary>
		/// Gets or sets the latitude
		/// </summary>
		[Property(MaxLength = 50, NotNull = true)]
		public string Latitude { get; set; } = "";

		/// <summary>
		/// Gets or sets the longitude
		/// </summary>
		[Property(MaxLength = 50, NotNull = true)]
		public string Longitude { get; set; } = "";
		#endregion

		#region IBusinessEntity properties
		[JsonIgnore, BsonIgnore, Ignore]
		public override string Title { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string SystemID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string EntityID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

	}
}