using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Core.Interfaces;
using MyApp.Core.Models.Configuration;

namespace MyApp.Infrastructure.Services
{
    /// <summary>
    /// Provides AES encryption and decryption for sensitive data
    /// </summary>
    public class AesEncryptionService : IEncryptionService
    {
        private readonly ILogger<AesEncryptionService> _logger;
        private readonly byte[] _key;
        private readonly byte[] _iv;

        public AesEncryptionService(
            IOptions<EncryptionOptions> options,
            ILogger<AesEncryptionService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            var encryptionOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
            
            if (string.IsNullOrEmpty(encryptionOptions.Key) || string.IsNullOrEmpty(encryptionOptions.IV))
            {
                _logger.LogError("Encryption configuration is missing Key or IV");
                throw new InvalidOperationException("Encryption configuration is invalid or incomplete");
            }

            try
            {
                // Convert the hex string key and IV to byte arrays
                _key = Convert.FromHexString(encryptionOptions.Key);
                _iv = Convert.FromHexString(encryptionOptions.IV);

                // Validate key and IV lengths
                if (_key.Length != 32) // 256 bits
                {
                    throw new ArgumentException("Encryption key must be 64 hex characters (32 bytes/256 bits)");
                }

                if (_iv.Length != 16) // 128 bits
                {
                    throw new ArgumentException("Encryption IV must be 32 hex characters (16 bytes/128 bits)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize encryption service");
                throw new InvalidOperationException("Could not initialize encryption service", ex);
            }
        }

        /// <summary>
        /// Encrypts the specified plaintext using AES-256
        /// </summary>
        public string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            try
            {
                using var aes = Aes.Create();
                aes.Key = _key;
                aes.IV = _iv;
                
                using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                using var ms = new MemoryStream();
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                using (var sw = new StreamWriter(cs))
                {
                    sw.Write(plaintext);
                }

                // Return as base64 string for safe storage
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error encrypting data");
                throw new InvalidOperationException("Could not encrypt data", ex);
            }
        }

        /// <summary>
        /// Decrypts the specified ciphertext using AES-256
        /// </summary>
        public string Decrypt(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext))
            {
                return string.Empty;
            }

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(ciphertext);

                using var aes = Aes.Create();
                aes.Key = _key;
                aes.IV = _iv;
                
                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var ms = new MemoryStream(cipherBytes);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);
                
                return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error decrypting data");
                throw new InvalidOperationException("Could not decrypt data", ex);
            }
        }
    }
}