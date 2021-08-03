#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
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

			if (ConfigurationManager.GetSection("net.vieapps.services.iplocations.providers") is AppConfigurationSectionHandler svcConfig)
			{
				if (svcConfig.Section.SelectNodes("provider") is XmlNodeList svcProviders)
					providers = svcProviders.ToList()
						.Select(svcProvider => new Provider(svcProvider.Attributes["name"]?.Value, svcProvider.Attributes["uriPattern"]?.Value, svcProvider.Attributes["accessKey"]?.Value ?? ""))
						.Where(provider => !string.IsNullOrWhiteSpace(provider.Name) && !string.IsNullOrWhiteSpace(provider.UriPattern))
						.ToDictionary(provider => provider.Name, provider => provider, StringComparer.OrdinalIgnoreCase);
				firstProviderName = svcConfig.Section.Attributes["first"]?.Value ?? "ipstack";
				secondProviderName = svcConfig.Section.Attributes["second"]?.Value ?? "ipapi";
				sameLocationRegex = svcConfig.Section.Attributes["sameLocationRegex"]?.Value ?? @"\d{1,3}\.\d{1,3}\.\d{1,3}";
			}

			Utility.Providers = providers;
			Utility.FirstProvider = providers.TryGetValue(firstProviderName, out Provider defaultProvider) ? defaultProvider : providers.FirstOrDefault().Value;
			Utility.SecondProvider = providers.TryGetValue(secondProviderName, out defaultProvider) ? defaultProvider : providers.LastOrDefault().Value;
			Utility.SameLocationRegex = new Regex(sameLocationRegex);
		}

		internal static List<IPAddress> PublicAddresses { get; } = new List<IPAddress>();

		internal static List<IPAddress> LocalAddresses { get; } = new List<IPAddress>();

		internal static async Task<IPAddress> GetByDynDnsAsync(CancellationToken cancellationToken = default)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await UtilityService.FetchHttpAsync("http://checkip.dyndns.org/", cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPAddress> GetByIpifyAsync(CancellationToken cancellationToken = default)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await UtilityService.FetchHttpAsync("http://api.ipify.org/", cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPLocation> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken = default)
		{
			var json = JObject.Parse(await UtilityService.FetchHttpAsync(Utility.Providers["ipstack"].GetUrl(ipAddress), cancellationToken).ConfigureAwait(false));
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

		internal static async Task<IPLocation> GetByIpApiAsync(string ipAddress, CancellationToken cancellationToken = default)
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

		internal static async Task<IPLocation> GetByKeyCdnAsync(string ipAddress, CancellationToken cancellationToken = default)
		{
			var json = JObject.Parse(await UtilityService.FetchHttpAsync(Utility.Providers["keycdn"].GetUrl(ipAddress), cancellationToken).ConfigureAwait(false));
			if ("success" != json.Get<string>("status"))
				throw new RemoteServerErrorException(json.Get<string>("description"), null);
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

		internal static Task<IPLocation> GetAsync(string providerName, string ipAddress, CancellationToken cancellationToken = default)
		{
			switch ((providerName ?? "ipstack").ToLower())
			{
				case "ipstack":
					return Utility.GetByIpStackAsync(ipAddress, cancellationToken);

				case "keycdn":
					return Utility.GetByKeyCdnAsync(ipAddress, cancellationToken);

				case "ipapi":
				default:
					return Utility.GetByIpApiAsync(ipAddress, cancellationToken);
			}
		}

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default, ILogger logger = null, string userID = null)
		{
			IPLocation location;
			try
			{
				location = await IPLocation.GetAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				logger?.LogError($"Error occurred while fetching IP address from database [\"{ipAddress}\"] => {ex.Message}", ex);
				location = await Utility.Cache.FetchAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
			}

			var callUpdateMethod = location != null;

			if (location == null || (DateTime.Now - location.LastUpdated).Days > 30)
				try
				{
					location = await Utility.GetAsync(Utility.FirstProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
					if (string.IsNullOrWhiteSpace(location.City))
						location = await Utility.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
					location.LastUpdated = DateTime.Now;

					try
					{
						await (callUpdateMethod ? IPLocation.UpdateAsync(location, userID, cancellationToken) : IPLocation.CreateAsync(location, cancellationToken)).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						logger?.LogError($"Error occurred while processing with database while working with \"{Utility.FirstProvider?.Name}\" provider => {ex.Message}", ex);
						if (location != null)
							await Utility.Cache.SetAsync(location, cancellationToken).ConfigureAwait(false);
						try
						{
							await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
						}
						catch { }
					}
				}
				catch (Exception fe)
				{
					logger?.LogError($"Error occurred while processing with \"{Utility.FirstProvider?.Name}\" provider => {fe.Message}", fe);
					try
					{
						location = await Utility.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
						location.LastUpdated = DateTime.Now;
						try
						{
							await (callUpdateMethod ? IPLocation.UpdateAsync(location, userID, cancellationToken) : IPLocation.CreateAsync(location, cancellationToken)).ConfigureAwait(false);
						}
						catch (Exception ex)
						{
							logger?.LogError($"Error occurred while processing with database while working with \"{Utility.SecondProvider?.Name}\" provider => {ex.Message}", ex);
							if (location != null)
								await Utility.Cache.SetAsync(location, cancellationToken).ConfigureAwait(false);
							try
							{
								await IPLocation.UpdateAsync(location, userID, cancellationToken).ConfigureAwait(false);
							}
							catch { }
						}
					}
					catch (Exception se)
					{
						logger?.LogError($"Error occurred while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se);
						location = location ?? new IPLocation
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

		internal static Task<IPLocation> GetCurrentLocationAsync(CancellationToken cancellationToken = default, ILogger logger = null, string userID = null)
		{
			IPAddress ipAddress = null;
			foreach (var address in Utility.PublicAddresses)
				if ($"{address}".IndexOf(".") > 0 || $"{address}".IndexOf(":") > 0)
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

		internal static async Task PrepareAddressesAsync(CancellationToken cancellationToken = default, ILogger logger = null)
		{
			try
			{
				Utility.PrepareProviders();
			}
			catch (Exception ex)
			{
				logger?.LogError($"Error occurred while preparing providers => {ex.Message}", ex);
			}

			try
			{
				var ipAddresses = await Dns.GetHostAddressesAsync(Dns.GetHostName()).WithCancellationToken(cancellationToken).ConfigureAwait(false);
				ipAddresses.ForEach(ipAddress =>
				{
					if (Utility.LocalAddresses.Find(ipAddress) == null)
						Utility.LocalAddresses.Add(ipAddress);
				});
			}
			catch (Exception ex)
			{
				logger?.LogError($"Error occurred while preparing local IP addresses => {ex.Message}", ex);
			}

			async Task getByDynDnsAsync()
			{
				try
				{
					var ipAddress = await Utility.GetByDynDnsAsync(cancellationToken).ConfigureAwait(false);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch (Exception ex)
				{
					logger?.LogError($"Error occurred while getting IP address by DynDNS => {ex.Message}", ex);
				}
			}

			async Task getByIpifyAsync()
			{
				try
				{
					var ipAddress = await Utility.GetByIpifyAsync(cancellationToken).ConfigureAwait(false);
					if (Utility.PublicAddresses.Find(ipAddress) == null)
						Utility.PublicAddresses.Add(ipAddress);
				}
				catch (Exception ex)
				{
					logger?.LogError($"Error occurred while getting IP address by IPify => {ex.Message}", ex);
				}
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