using System.Security.Cryptography;
using System.Text;

namespace Library.Extension_Methods
{
    public class DecryptExtensions
    {
        public static byte[] Decrypt(byte[] bytesToBeDecrypted, byte[] passwordBytes)
        {
            try
            {
                byte[]? decryptBytes = null;


                // Set your salt here, change it to meet your flavor:
                // The salt bytes must be at least 8 bytes.
                var saltBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

                using (MemoryStream ms = new MemoryStream())
                {
                    using (RijndaelManaged AES = new RijndaelManaged())
                    {
                        var key = new Rfc2898DeriveBytes(passwordBytes, saltBytes, 1000);

                        AES.KeySize = 256;
                        AES.BlockSize = 128;
                        AES.Key = key.GetBytes(AES.KeySize / 8);
                        AES.IV = key.GetBytes(AES.KeySize / 8);

                        AES.Mode = CipherMode.CBC;

                        using (var cs = new CryptoStream(ms, AES.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(bytesToBeDecrypted, 0, bytesToBeDecrypted.Length);
                            cs.Close();
                        }
                    }
                }
                return decryptBytes;

            }
            catch
            {
                return null;
            }
        }
        public static byte[] Decrypt(string encryptedText,
                                string password)
        {
            if (encryptedText == null)
            {
                return null;
            }
            if (password == null)
            {
                return null;
            }

            //Get the bytes of the string
            var bytesToBeDecrypted = Convert.FromBase64String(encryptedText);
            var passwordBytes = Encoding.UTF8.GetBytes(password);

            passwordBytes = SHA256.Create().ComputeHash(passwordBytes);

            var bytesDecrypted = Decrypt(bytesToBeDecrypted, passwordBytes);
            return bytesDecrypted;
        }


    }
}
