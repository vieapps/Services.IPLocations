#region Related components
using System;
using System.Linq;
using System.Net;
using System.Dynamic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Numerics;
using System.IO.Compression;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public class ServiceComponent : ServiceBase
	{
		public ServiceComponent() : base() { }

		public override string ServiceName => "IPLocations";

		#region Encryption Keys
		internal string ActivationKey => this.GetKey("Activation", "VIEApps-56BA2999-NGX-A2E4-Services-4B54-Activation-83EB-Key-693C250DC95D");

		internal string AuthenticationKey => this.GetKey("Authentication", "VIEApps-65E47754-NGX-50C0-Services-4565-Authentication-BA55-Key-A8CC23879C5D");

		internal BigInteger ECCKey => this.GetKey("ECC", "tRZMCCemDIshR6SBnltv/kZvamQfMuMyx+2DG+2Yuw+13xN4A7Kk+nmEM81kx6ISlaxGgJjr/xK9kWznIC3OWlF2yrdMKeeCPM8eVFIfkiGqIPnGPDJaWRbtGswNjMmfQhbQvQ9qa5306RLt9F94vrOQp2M9eojE3cSuTqNg4OTL+9Dddabgzl94F3gOJoPRxzHqyKWRUhQdP+hOsWSS2KTska2ddm/Zh/fGKXwY9lnnrLHY1wjSJqCS3OO7PCRfQtEWSJcvzzgm7bvJ18fOLuJ5CZVThS+XLNwZgkbcICepRCiVbsk6fmh0482BJesG55pVeyv7ZyKNW+RyMXNEyLn5VY/1lPLxz7lLS88Lvqo=").Base64ToBytes().Decrypt().ToUnsignedBigInteger();

		internal string RSAKey => this.GetKey("RSA", "NihT0EJ2NLRhmGNbZ8A3jUdhZfO4jG4hfkwaHF1o00YoVx9S61TpmMiaZssOZB++UUyNsZZzxSfkh0i5O9Yr9us+/2zXhgR2zQVxOUrZnPpHpspyJzOegBpMMuTWF4WTl7st797BQ0AmUY1nEjfMTKVP+VSrrx0opTgi93MyvRGGa48vd7PosAM8uq+oMkhMZ/jTvasK6n3PKtb9XAm3hh4NFZBf7P2WuACXZ4Vbzd1MGtLHWfrYnWjGI9uhlo2QKueRLmHoqKM5pQFlB9M7/i2D/TXeWZSWNU+vW93xncUght3QtCwRJu7Kp8UGf8nnrFOshHgvMgsdDlvJt9ECN0/2uyUcWzB8cte5C9r6sP6ClUVSkKDvEOJVmuS2Isk72hbooPaAm7lS5NOzb2pHrxTKAZxaUyiZkFXH5rZxQ/5QjQ9PiAzm1AVdBE1tg1BzyGzY2z7RY/iQ5o22hhRSN3l49U4ftfXuL+LrGKnzxtVrQ15Vj9/pF7mz3lFy2ttTxJPccBiffi9LVtuUCo9BRgw7syn07gAqj1WXzuhPALwK6P6M1pPeFg6NEKLNWgRFE8GZ+dPhr2O0YCgDVuhJ+hDUxCDAEkZ0cQBiliHtjldJji1FnFMqg90QvFCuVCydq94Dnxdl9HSVMNC69i6H2GNfBuD9kTQ6gIOepc86YazDto8JljqEVOpkegusPENadLjpwOYCCslN1Y314B2g9vvZRwU3T+PcziBjym1ceagEEAObZ22Z/vhxBZ83Z2E1/RkbJqovIRKuHLCzU/4lBeTseJNlKPSACPuKAX08P4y5c+28WDrHv2+o7x9ISJe0SN1KmFMvv1xYtj/1NwOHQzfVjbpL46E0+Jr/IOOjh2CQhhUMm1GOEQAZ9n+b7a4diUPDG+BewAZvtd5gNX4zD0IKkJFwN+fBMWSHs0gs3jNz4RcYhH5IoHq27jrfM3cUlvBP9JpbZugNIh8ddZsUd4XQuCVZF+qlfRjY6lfEy4nXX48ianvdCqnBpkmRadG8qFLybkVS+s8RHcPwRkkzKQ4oGHdDeyiU8ZXnwvJ3IxDLoJV0xqKSRjhe9MxwdeN7VMSTNRAtQvqVvm6cL8KNbd2Hx1kPDEcqeUfVIeZ+zTIptO5GpjEMV+4gu338WG1RyEMAaiE536E+UR+0MqIe/Q==").Decrypt();
		#endregion

		#region Start
		public override void Start(string[] args = null, bool initializeRepository = true, Func<ServiceBase, Task> nextAsync = null)
		{
			base.Start(args, initializeRepository, async (service) =>
			{
				// prepare
				await Utility.PrepareAddressesAsynnc().ConfigureAwait(false);
				Utility.GetProviders();
				this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Local address: {string.Join(" - ", Utility.LocalAddresses)}");
				this.Logger.LogInformation($"Public address: {string.Join(" - ", Utility.PublicAddresses)}");

				// last action
				if (nextAsync != null)
					try
					{
						await nextAsync(service).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger.LogError("Error occurred while invoking the next action", ex);
					}
			});
		}
		#endregion

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stopwatch = Stopwatch.StartNew();
			this.Logger.LogInformation($"Begin request ({requestInfo.Verb} {requestInfo.URI}) [{requestInfo.CorrelationID}]");
			try
			{
				// check
				if (!requestInfo.Verb.IsEquals("GET"))
					throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.URI})");

				// prepare
				var ip = requestInfo.GetQueryParameter("ip");
				if (IPAddress.IsLoopback(IPAddress.Parse(ip)))
					return new IPLocation
					{
						ID = ip.GetMD5(),
						IP = ip,
						City = "Local",
						Region = "N/A",
						Country = "N/A",
						CountryCode = "N/A",
						Continent = "N/A",
						Latitude = "N/A",
						Longitude = "N/A",
					}.ToJson();

				if (ip.IndexOf(".") > 0)
				{
					var address = ip.ToArray('.').Take(0, 3).Join(".");
					foreach (var ipAddress in Utility.LocalAddresses)
					{
						var adr = $"{ipAddress}".ToArray('.').Take(0, 3).Join(".");
						if (address.Equals(adr))
							return new IPLocation
							{
								ID = ip.GetMD5(),
								IP = ip,
								City = "Local",
								Region = "N/A",
								Country = "N/A",
								CountryCode = "N/A",
								Continent = "N/A",
								Latitude = "N/A",
								Longitude = "N/A",
							}.ToJson();
					}
				}

				// find in database
				var json = (await IPLocation.GetAsync<IPLocation>(ip.GetMD5(), cancellationToken).ConfigureAwait(false))?.ToJson();

				// no recorded, then request to provider
				if (json == null)
					json = await this.GetAsync(ip, cancellationToken).ConfigureAwait(false);

				stopwatch.Stop();
				this.Logger.LogInformation($"Success response - Execution times: {stopwatch.GetElapsedTimes()} [{requestInfo.CorrelationID}]");
				if (this.IsDebugResultsEnabled)
					this.Logger.LogInformation(
						$"- Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
						$"- Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					);
				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}

		async Task<JObject> GetAsync(string ip, CancellationToken cancellationToken)
		{
			try
			{
				return await this.GetAsync(Utility.FirstProvider?.Name, ip, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception fe)
			{
				await this.WriteLogsAsync(UtilityService.NewUUID, $"Error while processing with \"{Utility.FirstProvider?.Name}\" provider: {fe.Message}", fe).ConfigureAwait(false);
				try
				{
					return await this.GetAsync(Utility.SecondProvider?.Name, ip, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception se)
				{
					await this.WriteLogsAsync(UtilityService.NewUUID, $"Error while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se).ConfigureAwait(false);
					return new IPLocation
					{
						ID = ip.GetMD5(),
						IP = ip,
						City = "N/A",
						Region = "N/A",
						Country = "N/A",
						CountryCode = "N/A",
						Continent = "N/A",
						Latitude = "N/A",
						Longitude = "N/A",
					}.ToJson();
				}
			}
		}

		Task<JObject> GetAsync(string providerName, string ip, CancellationToken cancellationToken)
		{
			switch (providerName.ToLower())
			{
				case "ipapi":
					return this.GetByIpApiAsync(ip, cancellationToken);

				case "ipstack":
				default:
					return this.GetByIpStackAsync(ip, cancellationToken);
			}
		}

		async Task<JObject> GetByIpStackAsync(string ip, CancellationToken cancellationToken)
		{
			// send request
			var provider = Utility.Providers["ipstack"];
			var url = provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ip).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(url, null, null, cancellationToken).ConfigureAwait(false));

			// update database
			var ipLocation = new IPLocation
			{
				ID = json.Get<string>("ip").GetMD5(),
				IP = json.Get<string>("ip"),
				City = json.Get<string>("city"),
				Region = json.Get<string>("region_name"),
				Country = json.Get<string>("country_name"),
				CountryCode = json.Get<string>("country_code"),
				Continent = json.Get<string>("continent_name"),
				Latitude = json.Get<string>("latitude"),
				Longitude = json.Get<string>("longitude"),
			};
			await IPLocation.CreateAsync(ipLocation, cancellationToken).ConfigureAwait(false);

			// response
			json = ipLocation.ToJson();
			return json;
		}

		async Task<JObject> GetByIpApiAsync(string ip, CancellationToken cancellationToken)
		{
			// send request
			var provider = Utility.Providers["ipapi"];
			var url = provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ip).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(url, null, null, cancellationToken).ConfigureAwait(false));

			// update database
			var continent = json.Get<string>("timezone");
			var ipLocation = new IPLocation
			{
				ID = json.Get<string>("query").GetMD5(),
				IP = json.Get<string>("query"),
				City = json.Get<string>("city"),
				Region = json.Get<string>("regionName"),
				Country = json.Get<string>("country"),
				CountryCode = json.Get<string>("countryCode"),
				Continent = continent.Left(continent.IndexOf("/")),
				Latitude = json.Get<string>("lat"),
				Longitude = json.Get<string>("lon"),
			};
			await IPLocation.CreateAsync(ipLocation, cancellationToken).ConfigureAwait(false);

			// response
			json = ipLocation.ToJson();
			return json;
		}
	}
}