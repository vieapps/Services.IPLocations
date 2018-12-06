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

using Microsoft.Extensions.Logging;

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

		internal static Dictionary<string, Provider> Providers { get; private set; } = null;

		internal static Provider FirstProvider { get; private set; } = null;

		internal static Provider SecondProvider { get; private set; } = null;

		internal static Regex PublicAddressRegex { get; } = new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");

		internal static Regex SameLocationRegex { get; private set; } = null;

		internal static void PrepareProviders()
		{
			var providers = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);
			var firstProviderName = "ipstack";
			var secondProviderName = "ipapi";
			var sameLocationRegex = @"\d{1,3}\.\d{1,3}\.\d{1,3}";
			if (ConfigurationManager.GetSection("net.vieapps.iplocations.providers") is AppConfigurationSectionHandler config)
			{
				if (config.Section.SelectNodes("provider") is XmlNodeList xmlProviders)
					xmlProviders.ToList()
						.Select(xmlProvider => new
						{
							Name = xmlProvider.Attributes["name"]?.Value,
							UriPattern = xmlProvider.Attributes["uriPattern"]?.Value,
							AccessKey = xmlProvider.Attributes["accessKey"]?.Value
						})
						.Where(info => !string.IsNullOrWhiteSpace(info.Name) && !string.IsNullOrWhiteSpace(info.UriPattern))
						.ForEach(info => providers[info.Name] = new Provider
						{
							Name = info.Name,
							UriPattern = info.UriPattern,
							AccessKey = info.AccessKey
						});

				firstProviderName = config.Section.Attributes["first"]?.Value ?? "ipstack";
				secondProviderName = config.Section.Attributes["second"]?.Value ?? "ipapi";
				sameLocationRegex = config.Section.Attributes["sameLocationRegex"]?.Value ?? @"\d{1,3}\.\d{1,3}\.\d{1,3}";
			}

			Utility.Providers = providers;
			Utility.FirstProvider = providers.ContainsKey(firstProviderName)
				? providers[firstProviderName]
				: providers.Count > 0
					? providers.ElementAt(0).Value
					: null;
			Utility.SecondProvider = providers.ContainsKey(secondProviderName)
				? providers[secondProviderName]
				: providers.Count > 1
					? providers.ElementAt(providers.Count - 1).Value
					: null;
			Utility.SameLocationRegex = new Regex(sameLocationRegex);
		}

		internal static List<IPAddress> PublicAddresses { get; } = new List<IPAddress>();

		internal static List<IPAddress> LocalAddresses { get; } = new List<IPAddress>();

		internal static async Task<IPAddress> GetByDynDnsAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await UtilityService.GetWebPageAsync("http://checkip.dyndns.org/", null, null, cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPAddress> GetByIpifyAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await UtilityService.GetWebPageAsync("http://api.ipify.org/", null, null, cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPLocation> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken = default(CancellationToken))
		{
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(Utility.Providers["ipstack"].GetUrl(ipAddress), null, null, cancellationToken).ConfigureAwait(false));
			return json["error"] is JObject error
				? throw new RemoteServerErrorException($"{error.Get<string>("info")} ({error.Get<string>("code")} - {error.Get<string>("type")})", null)
				: new IPLocation
				{
					ID = json.Get<string>("ip").GenerateUUID(),
					IP = json.Get<string>("ip"),
					City = json.Get<string>("city"),
					Region = json.Get<string>("region_name"),
					Country = json.Get<string>("country_name"),
					Continent = json.Get<string>("continent_name"),
					Latitude = json.Get<string>("latitude"),
					Longitude = json.Get<string>("longitude"),
				};
		}

		internal static async Task<IPLocation> GetByIpApiAsync(string ipAddress, CancellationToken cancellationToken = default(CancellationToken))
		{
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(Utility.Providers["ipapi"].GetUrl(ipAddress), null, null, cancellationToken).ConfigureAwait(false));
			var continent = json.Get<string>("timezone");
			return new IPLocation
			{
				ID = json.Get<string>("query").GenerateUUID(),
				IP = json.Get<string>("query"),
				City = json.Get<string>("city"),
				Region = json.Get<string>("regionName"),
				Country = json.Get<string>("country"),
				Continent = continent.Left(continent.IndexOf("/")),
				Latitude = json.Get<string>("lat"),
				Longitude = json.Get<string>("lon"),
			};
		}

		internal static Task<IPLocation> GetAsync(string providerName, string ipAddress, CancellationToken cancellationToken = default(CancellationToken))
		{
			switch ((providerName ?? "ipstack").ToLower())
			{
				case "ipstack":
					return Utility.GetByIpStackAsync(ipAddress, cancellationToken);

				case "ipapi":
				default:
					return Utility.GetByIpApiAsync(ipAddress, cancellationToken);
			}
		}

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null, string userID = null)
		{
			var location = await IPLocation.GetAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
			var isUpdate = location != null;
			if (!isUpdate || (DateTime.Now - location.LastUpdated).Days > 30)
				try
				{
					location = await Utility.GetAsync(Utility.FirstProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
					location.LastUpdated = DateTime.Now;
					try
					{
						if (isUpdate)
							await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
						else
							await IPLocation.CreateAsync(location, cancellationToken).ConfigureAwait(false);
					}
					catch
					{
						try
						{
							await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
						}
						catch { }
					}
				}
				catch (Exception fe)
				{
					logger.LogError($"Error while processing with \"{Utility.FirstProvider?.Name}\" provider: {fe.Message}", fe);
					try
					{
						location = await Utility.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
						location.LastUpdated = DateTime.Now;
						try
						{
							if (isUpdate)
								await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
							else
								await IPLocation.CreateAsync(location, cancellationToken).ConfigureAwait(false);
						}
						catch
						{
							try
							{
								await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
							}
							catch { }
						}
					}
					catch (Exception se)
					{
						logger.LogError($"Error while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se);
						location = new IPLocation
						{
							ID = ipAddress.GenerateUUID(),
							IP = ipAddress,
							City = "N/A",
							Region = "N/A",
							Country = "N/A",
							Continent = "N/A",
							Latitude = "N/A",
							Longitude = "N/A",
						};
					}
				}
			return location;
		}

		internal static Task<IPLocation> GetCurrentLocationAsync(CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null, string userID = null)
		{
			IPAddress ipAddress = null;
			foreach (var address in Utility.PublicAddresses)
				if ($"{address}".IndexOf(".") > 0)
				{
					ipAddress = address;
					break;
				}
			return Utility.GetLocationAsync($"{ipAddress ?? Utility.PublicAddresses[0]}", cancellationToken, logger, userID);
		}

		internal static bool IsSameLocation(string ip)
		{
			if (IPAddress.IsLoopback(IPAddress.Parse(ip)))
				return true;

			var ipMatched = Utility.SameLocationRegex.Match(ip);
			var ipAddress = ipMatched.Success
				? ipMatched.Groups[0].Value
				: null;

			if (string.IsNullOrWhiteSpace(ipAddress))
				return false;

			foreach (var localAddress in Utility.LocalAddresses)
			{
				var localMatched = Utility.SameLocationRegex.Match($"{localAddress}");
				if (ipAddress.IsEquals(localMatched.Success ? localMatched.Groups[0].Value : null))
					return true;
			}

			return false;
		}

		internal static IPAddress Find(this List<IPAddress> addresses, IPAddress address)
			=> addresses.FirstOrDefault(adr => $"{address}".Equals($"{adr}"));

		internal static string GetUrl(this Provider provider, string ipAddress)
			=> provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ipAddress).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);

		internal static async Task PrepareAddressesAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			Utility.PrepareProviders();
			Dns.GetHostAddresses(Dns.GetHostName()).ForEach(ipAddress =>
			{
				if (Utility.LocalAddresses.Find(ipAddress) == null)
					Utility.LocalAddresses.Add(ipAddress);
			});

			async Task getByDynDnsAsync()
			{
				try
				{
					var ipAddress = await Utility.GetByDynDnsAsync(cancellationToken).ConfigureAwait(false);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch { }
			}

			async Task getByIpifyAsync()
			{
				try
				{
					var ipAddress = await Utility.GetByIpifyAsync(cancellationToken).ConfigureAwait(false);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch { }
			}

			await Task.WhenAny(getByDynDnsAsync(), getByIpifyAsync()).ConfigureAwait(false);
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