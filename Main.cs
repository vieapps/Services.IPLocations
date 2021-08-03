#region Related components
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "IPLocations";

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			// initialize caching storage
			Utility.Cache = new Components.Caching.Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());
			this.Syncable = false;

			// start the service
			base.Start(args, initializeRepository, async _ =>
			{
				// prepare
				await Utility.PrepareAddressesAsync(this.CancellationTokenSource.Token, this.Logger).ConfigureAwait(false);

				this.Logger.LogInformation($"Providers: {string.Join(", ", Utility.Providers.Keys)}");
				this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Expression of Same Location (Regex): {Utility.SameLocationRegex}");
				this.Logger.LogInformation($"Public Address: {string.Join(" - ", Utility.PublicAddresses)}");
				this.Logger.LogInformation($"Local Address: {string.Join(" - ", Utility.LocalAddresses)}");
				try
				{
					var currentLocation = await Utility.GetCurrentLocationAsync(this.CancellationTokenSource.Token, this.Logger).ConfigureAwait(false);
					this.Logger.LogInformation($"Current Location: {currentLocation.City}, {currentLocation.Region}, {currentLocation.Country}");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while fetching current location => {ex.Message}", ex);
				}

				// last action
				next?.Invoke(this);
			});
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			await this.WriteLogsAsync(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})").ConfigureAwait(false);
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken))
				try
				{
					// prepare
					if (!requestInfo.Verb.IsEquals("GET"))
						throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");

					JObject json = null;
					switch (requestInfo.ObjectName.ToLower())
					{
						case "public":
						case "public-ips":
							var publicAddresses = new JArray();
							Utility.PublicAddresses.ForEach(addr => publicAddresses.Add(new JValue($"{addr}")));
							json = new JObject
							{
								{ "IPs", publicAddresses }
							};
							break;

						case "local":
						case "local-ips":
							var localAddresses = new JArray();
							Utility.LocalAddresses.ForEach(addr => localAddresses.Add(new JValue($"{addr}")));
							json = new JObject
							{
								{ "IPs", localAddresses }
							};
							break;

						case "current":
						case "currentlocation":
							json = (await Utility.GetCurrentLocationAsync(cts.Token, this.Logger, requestInfo.Session.User.ID).ConfigureAwait(false)).ToJson(obj => obj.Remove("LastUpdated"));
							break;

						default:
							// prepare
							var ipAddress = requestInfo.GetQueryParameter("ip-address") ?? requestInfo.Session.IP;
							if (string.IsNullOrWhiteSpace(ipAddress))
								throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");

							// same location
							json = (Utility.IsSameLocation(ipAddress)
								? new IPLocation
									{
										ID = ipAddress.GetMD5(),
										IP = ipAddress,
										City = "N/A",
										Region = "N/A",
										Country = "N/A",
										Continent = "N/A",
										Latitude = "N/A",
										Longitude = "N/A",
									}
								: await Utility.GetLocationAsync(ipAddress, cts.Token, this.Logger, requestInfo.Session.User.ID).ConfigureAwait(false)).ToJson(obj => obj.Remove("LastUpdated"));
							break;
					}

					// response
					stopwatch.Stop();
					await this.WriteLogsAsync(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
					if (this.IsDebugResultsEnabled)
						await this.WriteLogsAsync(requestInfo, $"- Request: {requestInfo.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {json?.ToString(this.JsonFormat)}").ConfigureAwait(false);
					return json;
				}
				catch (Exception ex)
				{
					throw this.GetRuntimeException(requestInfo, ex, stopwatch);
				}
		}
	}
}