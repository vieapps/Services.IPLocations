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
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public static class Utility
	{
		public static Components.Caching.Cache Cache { get; internal set; }

		internal static Dictionary<string, Provider> Providers { get; set; }

		internal static Provider FirstProvider { get; set; }

		internal static Provider SecondProvider { get; set; }

		internal static Regex PublicAddressRegex { get; } = new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");

		internal static Regex SameLocationRegex { get; set; } = new Regex(@"\d{1,3}\.\d{1,3}");

		internal static List<string> SameLocationAddress { get; set; }

		internal static string ExternalURI { get; set; }

		internal static string DefaultLocation { get; set; } = "Hanoi, Vietnam";

		internal static IPLocation CurrentLocation { get; set; }

		internal static CancellationToken CancellationToken { get; set; }

		internal static ConcurrentDictionary<string, IPLocation> IPLocations { get; } = [];

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
				if (ex is InformationExistedException || ex.InnerException is InformationExistedException)
					try
					{
						await IPLocation.UpdateAsync(ipLocation, userID, Utility.CancellationToken).ConfigureAwait(false);
					}
					catch
					{
						await Utility.Cache.SetAsync(ipLocation, Utility.CancellationToken).ConfigureAwait(false);
					}
				else
					logger?.LogError($"Error occurred while updating database => {ex.Message}", ex);
			}
			return ipLocation;
		}

		internal static async Task<IPLocation> GetLocationAsync(string ipAddress, ILogger logger, string userID, CancellationToken cancellationToken, string serviceName = null, string excludedNodeID = null)
		{
			var doUpdate = false;
			var doBroadcast = false;

			if (!Utility.IPLocations.TryGetValue(ipAddress, out var ipLocation))
			{
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
				doBroadcast = ipLocation != null;
			}

			if (ipLocation == null || string.IsNullOrWhiteSpace(ipLocation.City) || "N/A".IsEquals(ipLocation.City) || (DateTime.Now - ipLocation.LastUpdated).Days > 30)
			{
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
				if (ipLocation != null)
				{
					Utility.IPLocations[ipLocation.IP] = ipLocation;
					doBroadcast = true;
				}
			}

			if (doBroadcast && serviceName != null && excludedNodeID != null)
				ipLocation.Send(serviceName, excludedNodeID);

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

		internal static void Send(this IPLocation ipLocation, string serviceName, string excludedNodeID)
			=> new CommunicateMessage(serviceName)
			{
				Type = "Update",
				ExcludedNodeID = excludedNodeID,
				Data = ipLocation.ToJson()
			}.Send();

		internal static bool IsSameLocation(this string ip)
		{
			if (IPAddress.IsLoopback(IPAddress.Parse(ip)))
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