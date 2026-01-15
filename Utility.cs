#region Related components
using System;
using System.Linq;
using System.Net;
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

		internal static Dictionary<string, Provider> Providers { get; set; }

		internal static Provider FirstProvider { get; set; }

		internal static Provider SecondProvider { get; set; }

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

		internal static async Task<IPAddress> GetByDynDnsAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://checkip.dyndns.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPAddress> GetByIpifyAsync(CancellationToken cancellationToken)
			=> IPAddress.Parse(Utility.PublicAddressRegex.Matches(await new Uri("http://api.ipify.org/").FetchHttpAsync(cancellationToken).ConfigureAwait(false))[0].ToString());

		internal static async Task<IPLocation> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken)
		{
			var uri = new Uri(Utility.Providers["ipstack"].GetUrl(ipAddress));
			var json = JObject.Parse(await uri.FetchHttpAsync(cancellationToken).ConfigureAwait(false));
			if (json["error"] is JObject error)
				throw new RemoteServerException(HttpStatusCode.InternalServerError, false, "GET", uri, null, null, $"{error.Get<string>("info")} ({error.Get<string>("code")} - {error.Get<string>("type")})");
			var ip = json.Get<string>("ip");
			return string.IsNullOrWhiteSpace(ip)
				? null
				: new IPLocation
				{
					ID = ip.GenerateUUID(),
					IP = ip,
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
			var uri = new Uri(Utility.Providers["ipapi"].GetUrl(ipAddress));
			var json = JObject.Parse(await uri.FetchHttpAsync(cancellationToken).ConfigureAwait(false));
			var continent = json.Get<string>("timezone");
			var ip = json.Get<string>("query");
			return string.IsNullOrWhiteSpace(ip)
				? null
				: new IPLocation
				{
					ID = ip.GenerateUUID(),
					IP = ip,
					City = json.Get<string>("city"),
					Region = json.Get<string>("regionName"),
					Country = json.Get<string>("country"),
					Continent = continent.Left(continent.IndexOf('/')),
					Latitude = json.Get<string>("lat"),
					Longitude = json.Get<string>("lon"),
				};
		}

		internal static async Task<IPLocation> GetByKeyCdnAsync(string ipAddress, CancellationToken cancellationToken)
		{
			var uri = new Uri(Utility.Providers["keycdn"].GetUrl(ipAddress));
			var json = JObject.Parse(await uri.FetchHttpAsync(new Dictionary<string, string>{ ["User-Agent"] = $"keycdn-tools:{Utility.APIsURI}" }, 90, cancellationToken).ConfigureAwait(false));
			if (!"success".IsEquals(json.Get<string>("status")))
				throw new RemoteServerException(HttpStatusCode.InternalServerError, false, "GET", uri, null, null, json.Get<string>("description"));
			json = json.Get<JObject>("data")?.Get<JObject>("geo");
			var ip = json?.Get<string>("ip");
			return string.IsNullOrWhiteSpace(ip)
				? null
				: new IPLocation
				{
					ID = ip.GenerateUUID(),
					IP = ip,
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

		internal static async Task<IPLocation> GetAsync(string providerName, string ipAddress, bool force, CancellationToken cancellationToken)
		{
			if (!force && Utility.IPLocations.TryGetValue(ipAddress, out var ipLocation))
				return ipLocation;

			try
			{
				ipLocation = (providerName ?? "ipapi").ToLower() switch
				{
					"ipstack" => await Utility.GetByIpStackAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					"keycdn" => await Utility.GetByKeyCdnAsync(ipAddress, cancellationToken).ConfigureAwait(false),
					_ => await Utility.GetByIpApiAsync(ipAddress, cancellationToken).ConfigureAwait(false),
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

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, string provider, bool force, Action<string, Exception> onError, CancellationToken cancellationToken)
		{
			var doUpdate = false;
			var doBroadcast = false;
			IPLocation ipLocation = null;

			if (!force && !Utility.IPLocations.TryGetValue(ipAddress, out ipLocation))
			{
				try
				{
					ipLocation = await IPLocation.GetAsync<IPLocation>(ipAddress.GenerateUUID(), cancellationToken).ConfigureAwait(false);
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
					try
					{
						ipLocation = await Utility.GetAsync(provider ?? Utility.FirstProvider?.Name, ipAddress, force, cancellationToken).ConfigureAwait(false);
						if (string.IsNullOrWhiteSpace(ipLocation.City))
							ipLocation = await Utility.GetAsync(provider ?? Utility.SecondProvider?.Name, ipAddress, force, cancellationToken).ConfigureAwait(false);
						ipLocation.SaveAsync(doUpdate, onError).Execute();
					}
					catch (OperationCanceledException) { }
					catch (Exception fe)
					{
						onError?.Invoke($"Error occurred while processing with \"{provider ?? Utility.FirstProvider?.Name}\" provider => {fe.Message}", fe);
						try
						{
							ipLocation = await Utility.GetAsync(provider ?? Utility.SecondProvider?.Name, ipAddress, force, cancellationToken).ConfigureAwait(false);
							ipLocation.SaveAsync(doUpdate, onError).Execute();
						}
						catch (OperationCanceledException) { }
						catch (Exception se)
						{
							onError?.Invoke($"Error occurred while processing with \"{provider ?? Utility.SecondProvider?.Name}\" provider: {se.Message}", se);
						}
					}
					finally
					{
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