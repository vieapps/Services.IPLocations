using System;
using System.Diagnostics;
using System.Xml.Serialization;
using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;

namespace net.vieapps.Services.IPLocations
{
	[BsonIgnoreExtraElements, DebuggerDisplay("IP = {IP}, City = {City}, Country = {Country}")]
	[Entity(CollectionName = "IPLocations", TableName = "T_IPLocations", CacheClass = typeof(Utility), CacheName = "Cache")]
	public class IPLocation : Repository<IPLocation>
	{
		public IPLocation() : base() { }

		/// <summary>
		/// Gets or sets the IP address
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(UniqueIndexName = "Address")]
		public string IP { get; set; } = "";

		/// <summary>
		/// Gets or sets the city name
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "GEO")]
		public string City { get; set; } = "";

		/// <summary>
		/// Gets or sets the region name
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "GEO")]
		public string Region { get; set; } = "";

		/// <summary>
		/// Gets or sets the country
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "GEO")]
		public string Country { get; set; } = "";

		/// <summary>
		/// Gets or sets the continent name (earth area)
		/// </summary>
		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "GEO")]
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

		/// <summary>
		/// Gets or sets the last-updated time
		/// </summary>
		[Sortable(IndexName = "Time")]
		public DateTime LastUpdated { get; set; } = DateTime.Now.AddDays(-31);

		[Ignore, JsonIgnore, XmlIgnore, BsonIgnore]
		public override string Title { get; set; }

		[Ignore, JsonIgnore, XmlIgnore, BsonIgnore]
		public override string SystemID { get; set; }

		[Ignore, JsonIgnore, XmlIgnore, BsonIgnore]
		public override string RepositoryID { get; set; }

		[Ignore, JsonIgnore, XmlIgnore, BsonIgnore]
		public override string RepositoryEntityID { get; set; }

		[Ignore, JsonIgnore, XmlIgnore, BsonIgnore]
		public override Privileges OriginalPrivileges { get; set; }
	}
}