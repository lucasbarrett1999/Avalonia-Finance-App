namespace MyApp.Core.Interfaces
{
    /// <summary>
    /// Service for encrypting and decrypting sensitive data
    /// </summary>
    public interface IEncryptionService
    {
        /// <summary>
        /// Encrypts the specified plaintext
        /// </summary>
        /// <param name="plaintext">Text to encrypt</param>
        /// <returns>Encrypted text</returns>
        string Encrypt(string plaintext);
        
        /// <summary>
        /// Decrypts the specified ciphertext
        /// </summary>
        /// <param name="ciphertext">Text to decrypt</param>
        /// <returns>Decrypted text</returns>
        string Decrypt(string ciphertext);
    }
}