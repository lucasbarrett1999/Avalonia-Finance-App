namespace MyApp.Core.Models.Configuration
{
    /// <summary>
    /// Configuration options for data encryption
    /// </summary>
    public class EncryptionOptions
    {
        /// <summary>
        /// Section name in appsettings.json
        /// </summary>
        public const string Encryption = "Encryption";

        /// <summary>
        /// The encryption key as a hex string (64 characters for AES-256)
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The initialization vector as a hex string (32 characters for AES)
        /// </summary>
        public string IV { get; set; } = string.Empty;
    }
}