using Microsoft.AspNetCore.Http;
using Microsoft.Skype.Bots.Media;
using RecordingBot.Model.Constants;
using RecordingBot.Services.Contract;
using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace RecordingBot.Services.ServiceSetup
{
    public partial class AzureSettings : IAzureSettings
    {
        public string ServiceDnsName { get; set; }
        public string ServicePath {get;set;} = "/";
        public string ServiceCname { get; set; }
        public string CertificateThumbprint { get; set; }
        public string CertificatePath { get; set; }
        public string CertificatePassword { get; set; } = "";
        public Uri CallControlBaseUrl { get; set; }
        public Uri PlaceCallEndpointUrl { get; set; }
        public MediaPlatformSettings MediaPlatformSettings { get; private set; }
        public string AadAppId { get; set; }
        public string AadAppSecret { get; set; }
        public int InstancePublicPort { get; set; }
        public int InstanceInternalPort { get; set; }
        public int CallSignalingPort { get; set; }
        public int CallSignalingPublicPort {get;set;} = 443;
        public bool CaptureEvents { get; set; } = false;
        public string PodName { get; set; }
        public string MediaFolder { get; set; }
        public string EventsFolder { get; set; }
        public string TopicName { get; set; } = "recordingbotevents";
        public string RegionName { get; set; } = "australiaeast";
        public string TopicKey { get; set; }
        public AudioSettings AudioSettings { get; set; }
        public bool IsStereo { get; set; }
        public int WAVSampleRate { get; set; }
        public int WAVQuality { get; set; }
        public PathString PodPathBase { get; private set; }
        public X509Certificate2 Certificate { get; private set; }

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(ServiceCname))
            {
                ServiceCname = ServiceDnsName;
            }

            Certificate = LoadCertificate();

            int podNumber = 0;

            if (!string.IsNullOrEmpty(PodName))
            {
                _ = int.TryParse(PodNumberRegex().Match(PodName).Value, out podNumber);
            }

            // Create structured config objects for service.
            CallControlBaseUrl = new Uri($"https://{ServiceCname}{(CallSignalingPublicPort != 443 ? ":" + CallSignalingPublicPort : "")}{ServicePath}{podNumber}/{HttpRouteConstants.CALL_SIGNALING_ROUTE_PREFIX}/{HttpRouteConstants.ON_NOTIFICATION_REQUEST_ROUTE}");
            PodPathBase = $"{ServicePath}{podNumber}";

            MediaPlatformSettings = new MediaPlatformSettings
            {
                MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                {
                    Certificate = Certificate,
                    InstanceInternalPort = InstanceInternalPort,
                    InstancePublicIPAddress = IPAddress.Any,
                    InstancePublicPort = InstancePublicPort + podNumber,
                    ServiceFqdn = ServiceCname
                },
                ApplicationId = AadAppId,
            };

            // Initialize Audio Settings
            AudioSettings = new AudioSettings
            {
                WavSettings = (WAVSampleRate > 0) ? new WavSettings(WAVSampleRate, WAVQuality) : null
            };
        }

        /// <summary>
        /// Loads the certificate from a PFX file path if configured,
        /// otherwise falls back to searching the Windows certificate store by thumbprint.
        /// </summary>
        private X509Certificate2 LoadCertificate()
        {
            // Prefer file-based certificate (local development)
            if (!string.IsNullOrWhiteSpace(CertificatePath))
            {
                if (!System.IO.File.Exists(CertificatePath))
                    throw new System.IO.FileNotFoundException($"Certificate file not found at '{CertificatePath}'.", CertificatePath);

                return new X509Certificate2(CertificatePath, CertificatePassword ?? "");
            }

            // Fall back to certificate store lookup by thumbprint (production / container)
            if (!string.IsNullOrWhiteSpace(CertificateThumbprint))
            {
                foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
                {
                    using var store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(X509FindType.FindByThumbprint, CertificateThumbprint, validOnly: false);

                    if (certs.Count == 1)
                        return certs[0];
                }

                throw new CertNotFoundException($"No certificate with thumbprint {CertificateThumbprint} was found in the machine or user certificate store.")
                {
                    Thumbprint = CertificateThumbprint
                };
            }

            throw new InvalidOperationException(
                "No certificate configured. Set either AzureSettings:CertificatePath (file) or AzureSettings:CertificateThumbprint (store).");
        }

        [GeneratedRegex(@"\d+$")]
        private static partial Regex PodNumberRegex();
    }
}
