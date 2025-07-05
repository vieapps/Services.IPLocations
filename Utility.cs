#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public static class Utility
	{
		public static Components.Caching.Cache Cache { get; internal set; }

		internal static Dictionary<string, Provider> Providers { get; private set; }

		internal static Provider FirstProvider { get; private set; }

		internal static Provider SecondProvider { get; private set; }

		internal static Regex PublicAddressRegex { get; } = new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");

		internal static Regex SameLocationRegex { get; private set; }

		internal static List<string> SameLocationAddress { get; private set; }

		public static string ExternalURI { get; internal set; }

		public static IPLocation CurrentLocation { get; internal set; }

		public static CancellationToken CancellationToken { get; internal set; }

		internal static ConcurrentDictionary<string, IPLocation> IPLocations { get; } = [];

		internal static void PrepareProviders()
		{
			var providers = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);
			var firstProviderName = "ipstack";
			var secondProviderName = "ipapi";
			var sameLocationRegex = @"\d{1,3}\.\d{1,3}";
			var sameLocationAddress = "";

			if (ConfigurationManager.GetSection("net.vieapps.services.iplocations.providers") is AppConfigurationSectionHandler svcConfig)
			{
				if (svcConfig.Section.SelectNodes("provider") is XmlNodeList svcProviders)
					providers = svcProviders.ToList()
						.Select(svcProvider => new Provider(svcProvider.Attributes["name"]?.Value, svcProvider.Attributes["uriPattern"]?.Value, svcProvider.Attributes["accessKey"]?.Value ?? ""))
						.Where(provider => !string.IsNullOrWhiteSpace(provider.Name) && !string.IsNullOrWhiteSpace(provider.UriPattern))
						.ToDictionary(provider => provider.Name, provider => provider, StringComparer.OrdinalIgnoreCase);
				firstProviderName = svcConfig.Section.Attributes["first"]?.Value ?? "ipstack";
				secondProviderName = svcConfig.Section.Attributes["second"]?.Value ?? "ipapi";
				sameLocationRegex = svcConfig.Section.Attributes["sameLocationRegex"]?.Value ?? @"\d{1,3}\.\d{1,3}";
				sameLocationAddress = svcConfig.Section.Attributes["sameLocationAddress"]?.Value ?? "";
			}

			Utility.Providers = providers;
			Utility.FirstProvider = providers.TryGetValue(firstProviderName, out Provider defaultProvider) ? defaultProvider : providers.FirstOrDefault().Value;
			Utility.SecondProvider = providers.TryGetValue(secondProviderName, out defaultProvider) ? defaultProvider : providers.LastOrDefault().Value;
			Utility.SameLocationRegex = new Regex(sameLocationRegex);
			Utility.SameLocationAddress = sameLocationAddress.ToList("|");
		}

		internal static List<IPAddress> PublicAddresses { get; } = [];

		internal static List<IPAddress> LocalAddresses { get; } = [];

		internal static async Task<IPAddress> GetByDynDnsAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://checkip.dyndns.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPAddress> GetByIpifyAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://api.ipify.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPLocation> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken)
		{
			var uri = new Uri(Utility.Providers["ipstack"].GetUrl(ipAddress));
			var json = JObject.Parse(await uri.FetchHttpAsync(cancellationToken).ConfigureAwait(false));
			return json["error"] is JObject error
				? throw new RemoteServerException(HttpStatusCode.InternalServerError, false, "GET", uri, null, null, $"{error.Get<string>("info")} ({error.Get<string>("code")} - {error.Get<string>("type")})")
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

		internal static async Task<IPLocation> GetByIpApiAsync(string ipAddress, CancellationToken cancellationToken)
		{
			var json = JObject.Parse(await UtilityService.FetchHttpAsync(Utility.Providers["ipapi"].GetUrl(ipAddress), cancellationToken).ConfigureAwait(false));
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

		internal static async Task<IPLocation> GetByKeyCdnAsync(string ipAddress, CancellationToken cancellationToken)
		{
			var uri = new Uri(Utility.Providers["keycdn"].GetUrl(ipAddress));
			var json = JObject.Parse(await uri.FetchHttpAsync(cancellationToken).ConfigureAwait(false));
			if ("success" != json.Get<string>("status"))
				throw new RemoteServerException(HttpStatusCode.InternalServerError, false, "GET", uri, null, null, json.Get<string>("description"));
			json = json["data"]["geo"] as JObject;
			return new IPLocation
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

		internal static async Task<IPLocation> GetAsync(CancellationToken cancellationToken, string ipAddress = null)
		{
			var data = await new Uri($"{Utility.ExternalURI}/iplocations{(ipAddress != null ? $"?ip={ipAddress}" : "")}").FetchHttpAsync(cancellationToken).ConfigureAwait(false);
			return new IPLocation().Fill(data.ToJson(), ipLocation => ipLocation.LastUpdated = DateTime.Now);
		}

		internal static async Task<IPLocation> GetAsync(string providerName, string ipAddress, CancellationToken cancellationToken)
		{
			if (Utility.IPLocations.TryGetValue(ipAddress, out var ipLocation))
				return ipLocation;

			try
			{
				switch ((providerName ?? "ipstack").ToLower())
				{
					case "ipstack":
						ipLocation = await Utility.GetByIpStackAsync(ipAddress, cancellationToken).ConfigureAwait(false);
						break;

					case "keycdn":
						ipLocation = await Utility.GetByKeyCdnAsync(ipAddress, cancellationToken).ConfigureAwait(false);
						break;

					case "ipapi":
					default:
						ipLocation = await Utility.GetByIpApiAsync(ipAddress, cancellationToken).ConfigureAwait(false);
						break;
				}
				return Utility.IPLocations[ipLocation.IP] = ipLocation;
			}
			catch
			{
				if (!string.IsNullOrWhiteSpace(Utility.ExternalURI))
					try
					{
						ipLocation = await Utility.GetAsync(cancellationToken, ipAddress).ConfigureAwait(false);
						return Utility.IPLocations[ipLocation.IP] = ipLocation;
					}
					catch { }
				throw;
			}
		}

		internal static async Task<IPLocation> SaveAsync(this IPLocation ipLocation, bool doUpdate = false, ILogger logger = null, string userID = null)
		{
			ipLocation.LastUpdated = DateTime.Now;
			try
			{
				await (doUpdate ? IPLocation.UpdateAsync(ipLocation, userID, Utility.CancellationToken) : IPLocation.CreateAsync(ipLocation, Utility.CancellationToken)).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				logger?.LogError($"Error occurred while updating database => {ex.Message}", ex);
				await Utility.Cache.SetAsync(ipLocation, Utility.CancellationToken).ConfigureAwait(false);
				try
				{
					await IPLocation.UpdateAsync(ipLocation, userID, Utility.CancellationToken).ConfigureAwait(false);
				}
				catch { }
			}
			return ipLocation;
		}

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, ILogger logger, string userID, CancellationToken cancellationToken)
		{
			var doUpdate = false;
			if (!Utility.IPLocations.TryGetValue(ipAddress, out var ipLocation))
				try
				{
					ipLocation = await IPLocation.GetAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
					doUpdate = ipLocation != null;
					if (doUpdate)
						Utility.IPLocations[ipLocation.IP] = ipLocation;
				}
				catch (Exception ex)
				{
					logger.LogError($"Error occurred while fetching IP address from database [\"{ipAddress}\"] => {ex.Message}", ex);
					ipLocation = await Utility.Cache.FetchAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
				}

			if (ipLocation == null || string.IsNullOrWhiteSpace(ipLocation.City) || "N/A".IsEquals(ipLocation.City) || (DateTime.Now - ipLocation.LastUpdated).Days > 30)
				try
				{
					ipLocation = await Utility.GetAsync(Utility.FirstProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
					if (string.IsNullOrWhiteSpace(ipLocation.City))
						ipLocation = await Utility.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
					ipLocation.SaveAsync(doUpdate, logger, userID).Run();
				}
				catch (Exception fe)
				{
					logger?.LogError($"Error occurred while processing with \"{Utility.FirstProvider?.Name}\" provider => {fe.Message}", fe);
					try
					{
						ipLocation = await Utility.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
						ipLocation.SaveAsync(doUpdate, logger, userID).Run();
					}
					catch (Exception se)
					{
						logger.LogError($"Error occurred while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se);
					}
				}

			return ipLocation ?? new IPLocation
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

		internal static Task<IPLocation> GetCurrentLocationAsync(ILogger logger, CancellationToken cancellationToken, string userID = null)
		{
			var ipAddress = Utility.PublicAddresses.FirstOrDefault(address => $"{address}".IndexOf('.') > 0 || $"{address}".IndexOf(':') > 0);
			return ipAddress != null ? Utility.GetLocationAsync($"{ipAddress}", logger, userID, cancellationToken) : Task.FromResult<IPLocation>(null);
		}

		internal static bool IsSameLocation(this string ip)
		{
			if (IPAddress.IsLoopback(IPAddress.Parse(ip)))
				return true;

			var ipMatched = Utility.SameLocationRegex == null ? null : Utility.SameLocationRegex.Match(ip);
			var ipAddress = ipMatched != null && ipMatched.Success
				? ipMatched.Groups[0].Value
				: null;

			if (!string.IsNullOrWhiteSpace(ipAddress))
				foreach (var localAddress in Utility.LocalAddresses)
				{
					var localMatched = Utility.SameLocationRegex == null ? null : Utility.SameLocationRegex.Match($"{localAddress}");
					if (ipAddress.IsEquals(localMatched != null && localMatched.Success ? localMatched.Groups[0].Value : null))
						return true;
				}

			return (Utility.SameLocationAddress ?? []).FirstOrDefault(address => ip.StartsWith(address)) != null;
		}

		internal static IPAddress Find(this List<IPAddress> ipAddresses, IPAddress ipAddress)
			=> ipAddresses.FirstOrDefault(address => $"{ipAddress}".Equals($"{address}"));

		internal static string GetUrl(this Provider provider, string ipAddress)
			=> provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ipAddress).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);

		internal static async Task PrepareAddressesAsync(CancellationToken cancellationToken, ILogger logger, bool prepareProviders = true, bool prepareLocalAddresses = true)
		{
			if (prepareProviders)
				try
				{
					Utility.PrepareProviders();
				}
				catch (Exception ex)
				{
					logger.LogError($"Error occurred while preparing providers => {ex.Message}", ex);
				}

			if (prepareLocalAddresses)
				try
				{
					var ipAddresses = await Dns.GetHostAddressesAsync(Dns.GetHostName(), cancellationToken).ConfigureAwait(false);
					ipAddresses.ForEach(ipAddress =>
					{
						if (Utility.LocalAddresses.Find(ipAddress) == null)
							Utility.LocalAddresses.Add(ipAddress);
					});
				}
				catch (Exception ex)
				{
					logger.LogError($"Error occurred while preparing local IP addresses => {ex.Message}", ex);
				}

			async Task getByDynDnsAsync()
			{
				IPAddress ipAddress = null;
				try
				{
					ipAddress = await Utility.GetByDynDnsAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					logger.LogError($"Error occurred while getting public IP address by DynDNS => {ex.Message}", ex);
					if (!string.IsNullOrWhiteSpace(Utility.ExternalURI))
						try
						{
							ipAddress = (await Utility.GetAsync(cancellationToken).ConfigureAwait(false))?.IPAddress;
						}
						catch { }
				}
				if (ipAddress != null && Utility.PublicAddresses.Find(ipAddress) == null)
					Utility.PublicAddresses.Add(ipAddress);
			}

			async Task getByIpifyAsync()
			{
				IPAddress ipAddress = null;
				try
				{
					ipAddress = await Utility.GetByIpifyAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					logger.LogError($"Error occurred while getting public IP address by IPify => {ex.Message}", ex);
					if (!string.IsNullOrWhiteSpace(Utility.ExternalURI))
						try
						{
							ipAddress = (await Utility.GetAsync(cancellationToken).ConfigureAwait(false))?.IPAddress;
						}
						catch { }
				}
				if (ipAddress != null && Utility.PublicAddresses.Find(ipAddress) == null)
					Utility.PublicAddresses.Add(ipAddress);
			}

			await Task.WhenAny(getByDynDnsAsync(), getByIpifyAsync()).ConfigureAwait(false);
		}
	}

	//  --------------------------------------------------------------------------------------------

	internal class Provider
	{
		public Provider(string name = null, string uriPattern = null, string accessKey = null)
		{
			this.Name = name ?? "";
			this.UriPattern = uriPattern ?? "";
			this.AccessKey = accessKey ?? "";
		}
		public string Name { get; set; } = "";
		public string UriPattern { get; set; } = "";
		public string AccessKey { get; set; } = "";
	}

	//  --------------------------------------------------------------------------------------------

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}