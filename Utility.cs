#region Related components
using System;
using System.Linq;
using System.Net;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public static class Utility
	{
		public static Cache Cache { get; } = Cache.CreateInstance("VIEApps-Services-IPLocations", Logger.GetLoggerFactory(), "true".IsEquals(UtilityService.GetAppSetting("IPLocations:Cache:L1")));

		internal static List<Provider> Providers { get; set; } = [];

		internal static string DefaultProvider { get; set; }

		internal static Regex PublicAddressRegex { get; } = new(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");

		internal static Regex SameLocationRegex { get; set; } = new(@"\d{1,3}\.\d{1,3}");

		internal static List<string> SameLocationAddress { get; set; }

		internal static string APIsURI { get; set; }

		internal static string ExternalURI { get; set; }

		internal static string DefaultLocation { get; set; } = "Hanoi, Vietnam";

		internal static IPLocation CurrentLocation { get; set; }

		internal static CancellationToken CancellationToken { get; set; }

		internal static ConcurrentDictionary<string, IPLocation> IPLocations { get; } = [];

		internal static ConcurrentHashSet<string> Fetching { get; } = [];

		internal static List<IPAddress> PublicAddresses { get; } = [];

		internal static List<IPAddress> LocalAddresses { get; } = [];

		internal static async Task<IPLocation> GetIpAsync(string providerName, string ipAddress, Func<ExpandoObject, (string Message, string Code, string Type)> getError, Func<ExpandoObject, string> getIP, Func<ExpandoObject, string> getCity, Func<ExpandoObject, string> getRegion, Func<ExpandoObject, string> getCountry, Func<ExpandoObject, string> getContinent, Func<ExpandoObject, string> getLatitude, Func<ExpandoObject, string> getLongitude, CancellationToken cancellationToken)
		{
			var provider = Utility.Providers.FirstOrDefault(prvdr => prvdr.Name.IsEquals(providerName));
			var uri = provider != null ? new Uri(provider.GetUrl(ipAddress)) : null;
			var data = provider != null ? JObject.Parse(await uri.FetchHttpAsync("keycdn".IsEquals(providerName) ? new Dictionary<string, string> { ["User-Agent"] = $"keycdn-tools:{Utility.APIsURI}" } : null, 15, cancellationToken).ConfigureAwait(false)).ToExpandoObject() : null;
			var error = getError(data);
			if (!string.IsNullOrWhiteSpace(error.Message) || !string.IsNullOrWhiteSpace(error.Code) || !string.IsNullOrWhiteSpace(error.Type))
				throw new RemoteServerException(HttpStatusCode.InternalServerError, false, "GET", uri, null, null, $"{error.Message} ({error.Code} - {error.Type})");
			var ip = getIP != null ? getIP(data) : data?.Get<string>("ip");
			var city = getCity != null ? getCity(data) : data?.Get<string>("city");
			var continent = getContinent != null ? getContinent(data) : data?.Get<string>("continent_name");
			return string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(city)
				? null
				: new IPLocation
				{
					ID = ip.GenerateUUID(),
					IP = ip,
					City = city,
					Region = getRegion != null ? getRegion(data) : data.Get<string>("region_name"),
					Country = getCountry != null ? getCountry(data) : data.Get<string>("country_name"),
					Continent = !string.IsNullOrWhiteSpace(continent) && continent.IndexOf('/') > 0 ? continent.Left(continent.IndexOf('/')) : continent ?? "N/A",
					Latitude = getLatitude != null ? getLatitude(data) : data.Get<string>("latitude"),
					Longitude = getLongitude != null ? getLongitude(data) : data.Get<string>("longitude")
				};
		}

		internal static async Task<IPAddress> GetByDynDnsAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://checkip.dyndns.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPAddress> GetByIpifyAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://api.ipify.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static Task<IPLocation> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("ipstack", ipAddress, data => (data?.Get<string>("error.info"), data?.Get<string>("error.code"), data?.Get<string>("error.type")), null, null, null, null, null, null, null, cancellationToken);

		internal static Task<IPLocation> GetByIpApiCoAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("ipapi.co", ipAddress, data => (null, null, null), null, null, data => data?.Get<string>("region"), null, data => data?.Get<string>("timezone"), null, null, cancellationToken);

		internal static Task<IPLocation> GetByIpApiComAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("ip-api.com", ipAddress, data => (data?.Get<string>("error.info"), data?.Get<string>("error.code"), data?.Get<string>("error.type")), data => data?.Get<string>("query"), null, data => data?.Get<string>("regionName"), data => data?.Get<string>("country"), data => data?.Get<string>("timezone"), data => data?.Get<string>("lat"), data => data?.Get<string>("lon"), cancellationToken);

		internal static Task<IPLocation> GetByKeyCdnAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("keycdn", ipAddress, data => "success".IsEquals(data.Get<string>("status")) ? (null, null, null) : (data?.Get<string>("description"), "500", data?.Get<string>("status")), null, null, null, null, data => data?.Get<string>("geo.continent_name"), null, null, cancellationToken);

		internal static Task<IPLocation> GetByIpWhoisAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("ipwhois", ipAddress, data => (null, null, null), null, null, data => data?.Get<string>("region"), data => data?.Get<string>("country"), data => data?.Get<string>("timezone.id"), null, null, cancellationToken);

		internal static Task<IPLocation> GetByFindIpAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("findip", ipAddress, data => (null, null, null), _ => ipAddress, data => data?.Get<string>("city.names.en"), data => data?.ToJson()?.Get<JArray>("subdivisions")?.FirstOrDefault()?.Get<JObject>("names")?.Get<string>("en") ?? data?.Get<string>("city.names.en"), data => data?.Get<string>("country.names.en"), data => data?.Get<string>("continent.names.en"), data => data?.Get<string>("location.latitude"), data => data?.Get<string>("location.longitude"), cancellationToken);

		internal static Task<IPLocation> GetByGeoLocationAsync(string ipAddress, CancellationToken cancellationToken)
			=> Utility.GetIpAsync("geolocation", ipAddress, data => (null, null, null), _ => ipAddress, data => data?.Get<string>("location.district"), data => data?.Get<string>("location.city"), data => data?.Get<string>("location.country_name"), data => data?.Get<string>("location.continent_name"), data => data?.Get<string>("location.latitude"), data => data?.Get<string>("location.longitude"), cancellationToken);

		internal static async Task<IPLocation> GetAsync(CancellationToken cancellationToken, string ipAddress = null)
		{
			var data = await new Uri($"{Utility.ExternalURI}/iplocations{(ipAddress != null ? $"?ip={ipAddress}" : "")}").FetchHttpAsync(cancellationToken).ConfigureAwait(false);
			return new IPLocation().Fill(data.ToJson(), ipLocation => ipLocation.LastUpdated = DateTime.Now);
		}

		internal static async Task<IPLocation> GetAsync(Provider provider, string ipAddress, bool force, CancellationToken cancellationToken)
		{
			if (!force && Utility.IPLocations.TryGetValue(ipAddress, out var ipLocation))
				return ipLocation;

			try
			{
				ipLocation = (provider?.Name ?? Utility.DefaultProvider).ToLower() switch
				{
					"ipwhois" => await Utility.GetByIpWhoisAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"ipstack" => await Utility.GetByIpStackAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"ipapi.co" => await Utility.GetByIpApiCoAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"ip-api.com" => await Utility.GetByIpApiComAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"findip" => await Utility.GetByFindIpAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"geolocation" => await Utility.GetByGeoLocationAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"keycdn" => await Utility.GetByKeyCdnAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					_ => await Utility.GetByIpWhoisAsync(ipAddress, cancellationToken).ConfigureAwait(false)
				};
				return ipLocation == null ? null : Utility.IPLocations[ipLocation.IP] = ipLocation;
			}
			catch (OperationCanceledException)
			{
				return null;
			}
			catch (Exception)
			{
				if (!force && !string.IsNullOrWhiteSpace(Utility.ExternalURI))
					try
					{
						ipLocation = await Utility.GetAsync(cancellationToken, ipAddress).ConfigureAwait(false);
						return Utility.IPLocations[ipLocation.IP] = ipLocation;
					}
					catch { }
				throw;
			}
		}

		internal static async Task<IPLocation> SaveAsync(this IPLocation ipLocation, bool doUpdate = false, Action<string, Exception> onError = null)
		{
			ipLocation.LastUpdated = DateTime.Now;
			try
			{
				await (doUpdate ? IPLocation.UpdateAsync(ipLocation, true, Utility.CancellationToken) : IPLocation.CreateAsync(ipLocation, Utility.CancellationToken)).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (ex is InformationExistedException || ex.InnerException is InformationExistedException)
					try
					{
						await IPLocation.UpdateAsync(ipLocation, true, Utility.CancellationToken).ConfigureAwait(false);
					}
					catch
					{
						await Utility.Cache.SetAsync(ipLocation, Utility.CancellationToken).ConfigureAwait(false);
					}
				else
					onError?.Invoke($"Error occurred while updating database => {ex.Message}", ex);
			}
			return ipLocation;
		}

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, string providerName, bool force, Action<string, Exception> onError, CancellationToken cancellationToken)
		{
			var doUpdate = false;
			var doBroadcast = false;

			if (force && Utility.IPLocations.TryRemove(ipAddress, out var ipLocation))
				await IPLocation.DeleteAsync(ipLocation.ID, null, cancellationToken).ConfigureAwait(false);

			if (!Utility.IPLocations.TryGetValue(ipAddress, out ipLocation))
			{
				try
				{
					ipLocation = await IPLocation.GetAsync(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
					doUpdate = ipLocation != null;
				}
				catch (Exception ex)
				{
					onError?.Invoke($"Error occurred while fetching IP address from database [\"{ipAddress}\"] => {ex.Message}", ex);
					ipLocation = await Utility.Cache.FetchAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
				}
				if (ipLocation != null)
				{
					Utility.IPLocations[ipLocation.IP] = ipLocation;
					doBroadcast = true;
				}
			}

			if (ipLocation == null)
			{
				while (Utility.Fetching.Contains(ipAddress))
					await Task.Delay(UtilityService.GetRandomNumber(123, 456), cancellationToken).ConfigureAwait(false);
				Utility.IPLocations.TryGetValue(ipAddress, out ipLocation);
			}

			if (ipLocation == null || string.IsNullOrWhiteSpace(ipLocation.City) || "N/A".IsEquals(ipLocation.City) || (DateTime.Now - ipLocation.LastUpdated).Days > 30)
			{
				if (Utility.Fetching.Add(ipAddress))
				{
					new CommunicateMessage(ServiceComponent.ServiceComponent.ServiceName)
					{
						Type = "Fetching",
						ExcludedNodeID = ServiceComponent.ServiceComponent.NodeID,
						Data = new JObject { ["IP"] = ipAddress }
					}.Send(Router.GotBackupRouter());

					Provider provider = null;
					var doFetch = true;
					while (doFetch)
						try
						{
							provider ??= Utility.Providers.FirstOrDefault(pvdr => pvdr.Name.IsEquals(providerName ?? Utility.DefaultProvider));
							ipLocation = await Utility.GetAsync(provider, ipAddress, force, cancellationToken).ConfigureAwait(false);
							if (ipLocation != null)
							{
								ipLocation.SaveAsync(doUpdate, onError).Execute();
								doFetch = false;
							}
						}
						catch (OperationCanceledException)
						{
							doFetch = false;
						}
						catch (Exception ex)
						{
							onError?.Invoke($"Error occurred while processing with \"{provider?.Name}\" provider on IP \"{ipAddress}\" => {ex.Message}", ex);
							var index = Utility.Providers.FindIndex(pvdr => pvdr.Name.Equals(provider.Name));
							if (index < Utility.Providers.Count - 1)
								provider = Utility.Providers[index + 1];
							else
								doFetch = false;
						}

					Utility.Fetching.TryRemove(ipAddress);
				}
				else
					Utility.IPLocations.TryGetValue(ipAddress, out ipLocation);

				if (ipLocation != null)
				{
					Utility.IPLocations[ipLocation.IP] = ipLocation;
					doBroadcast = true;
				}
			}

			if (doBroadcast)
				ipLocation.Send();

			new CommunicateMessage(ServiceComponent.ServiceComponent.ServiceName)
			{
				Type = "Fetched",
				ExcludedNodeID = ServiceComponent.ServiceComponent.NodeID,
				Data = new JObject { ["IP"] = ipAddress }
			}.Send(Router.GotBackupRouter());

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

		internal static Task<IPLocation> GetCurrentLocationAsync(Action<string, Exception> onError, CancellationToken cancellationToken)
		{
			var ipAddress = Utility.PublicAddresses.FirstOrDefault(address => $"{address}".IndexOf('.') > 0 || $"{address}".IndexOf(':') > 0);
			return ipAddress != null ? Utility.GetLocationAsync($"{ipAddress}", null, false, onError, cancellationToken) : Task.FromResult<IPLocation>(null);
		}

		internal static void Send(this IPLocation ipLocation)
			=> new CommunicateMessage(ServiceComponent.ServiceComponent.ServiceName)
			{
				Type = "Update",
				ExcludedNodeID = ServiceComponent.ServiceComponent.NodeID,
				Data = ipLocation.ToJson()
			}.Send(Router.GotBackupRouter());

		internal static bool IsSameLocation(this string ip)
		{
			if (ip.Equals("::1") || ip.Equals("127.0.0.1") || IPAddress.IsLoopback(IPAddress.Parse(ip)))
				return true;

			var ipMatched = Utility.SameLocationRegex?.Match(ip);
			var ipAddress = ipMatched != null && ipMatched.Success
				? ipMatched.Groups[0].Value
				: null;

			if (!string.IsNullOrWhiteSpace(ipAddress))
				foreach (var localAddress in Utility.LocalAddresses)
				{
					var localMatched = Utility.SameLocationRegex?.Match($"{localAddress}");
					if (ipAddress.IsEquals(localMatched != null && localMatched.Success ? localMatched.Groups[0].Value : null))
						return true;
				}

			return (Utility.SameLocationAddress ?? []).FirstOrDefault(address => ip.StartsWith(address)) != null;
		}

		internal static IPAddress Find(this List<IPAddress> ipAddresses, IPAddress ipAddress)
			=> ipAddresses.FirstOrDefault(address => $"{ipAddress}".Equals($"{address}"));

		internal static string GetUrl(this Provider provider, string ipAddress)
			=> provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ipAddress).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);

		internal static async Task PrepareAddressesAsync(CancellationToken cancellationToken, ILogger logger, bool prepareLocalAddresses = true)
		{
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

	internal class Provider(string name = null, string uriPattern = null, string accessKey = null)
	{
		public string Name { get; set; } = name ?? "";
		public string UriPattern { get; set; } = uriPattern ?? "";
		public string AccessKey { get; set; } = accessKey ?? "";
	}

	//  --------------------------------------------------------------------------------------------

	[Repository(ServiceName = "IPLocations")]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}