#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson.Serialization.Attributes;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public static class Utility
	{
		public static Cache Cache { get; } = new Cache("VIEApps-Services-IPLocations", UtilityService.GetAppSetting("Cache:ExpirationTime", "30").CastAs<int>(), false, UtilityService.GetAppSetting("Cache:Provider"), Logger.GetLoggerFactory());

		internal static Dictionary<string, Provider> Providers { get; } = Utility.GetProviders();

		internal static Provider FirstProvider { get; private set; } = null;

		internal static Provider SecondProvider { get; private set; } = null;

		internal static Dictionary<string, Provider> GetProviders()
		{
			var providers = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);
			if (ConfigurationManager.GetSection("net.vieapps.iplocations.providers") is AppConfigurationSectionHandler config)
			{
				if (config.Section.SelectNodes("provider") is XmlNodeList xmlProviders)
					xmlProviders.ToList().ForEach(xmlProvider => providers[xmlProvider.Attributes["name"].Value] = new Provider
					{
						Name = xmlProvider.Attributes["name"].Value,
						UriPattern = xmlProvider.Attributes["uriPattern"].Value,
						AccessKey = xmlProvider.Attributes["accessKey"].Value
					});

				var providerName = config.Section.Attributes["first"]?.Value ?? "ipstack";
				Utility.FirstProvider = providers.ContainsKey(providerName)
					? providers[providerName]
					: providers.Count > 0
						? providers.ElementAt(0).Value
						: null;

				providerName = config.Section.Attributes["second"]?.Value ?? "ipapi";
				Utility.SecondProvider = providers.ContainsKey(providerName)
					? providers[providerName]
					: providers.Count > 1
						? providers.ElementAt(providers.Count - 1).Value
						: null;
			}
			return providers;
		}

		internal static List<IPAddress> PublicAddresses { get; private set; } = new List<IPAddress>();

		internal static List<IPAddress> LocalAddresses { get; private set; } = new List<IPAddress>();

		internal static IPAddress Find(this List<IPAddress> addresses, IPAddress address) => addresses.FirstOrDefault(adr => $"{address}".Equals($"{adr}"));

		internal static async Task PrepareAddressesAsynnc()
		{
			async Task getIpByDynDnsAsync()
			{
				try
				{
					var ip = await new WebClient().DownloadStringTaskAsync("http://checkip.dyndns.org/").ConfigureAwait(false);
					ip = new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}").Matches(ip)[0].ToString();
					var ipAddress = IPAddress.Parse(ip);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch { }
			}

			async Task getIpByIpifyAsync()
			{
				try
				{
					var ip = await new WebClient().DownloadStringTaskAsync("http://api.ipify.org/").ConfigureAwait(false);
					var ipAddress = IPAddress.Parse(ip);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch { }
			}

			await Task.WhenAny(getIpByDynDnsAsync(), getIpByIpifyAsync()).ConfigureAwait(false);

			Dns.GetHostAddresses(Dns.GetHostName()).ForEach(ipAddress =>
			{
				if (Utility.LocalAddresses.Find(ipAddress) == null)
					Utility.LocalAddresses.Add(ipAddress);
			});
		}
	}

	//  --------------------------------------------------------------------------------------------

	internal class Provider
	{
		public string Name { get; set; } = "";
		public string UriPattern { get; set; } = "";
		public string AccessKey { get; set; } = "";
	}

	//  --------------------------------------------------------------------------------------------

	[Serializable]
	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class
	{
		[JsonIgnore, XmlIgnore, BsonIgnore, Ignore]
		public override string ServiceName => ServiceBase.ServiceComponent.ServiceName;
	}
}