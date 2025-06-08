// Utilities/RsaKeyLoader.cs

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace VirtuPathAPI.Utilities
{
    public static class RsaKeyLoader
    {
        public static JsonWebKey GetRsaPublicJwk(string publicPemPath)
        {
            // Read PEM file
            var pem = File.ReadAllText(publicPemPath).Trim();
            const string header = "-----BEGIN PUBLIC KEY-----";
            const string footer = "-----END PUBLIC KEY-----";

            if (!pem.StartsWith(header) || !pem.EndsWith(footer))
                throw new InvalidOperationException("Expected a PEM‐encoded SPKI RSA public key.");

            var base64 = pem
                .Replace(header, "")
                .Replace(footer, "")
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();
            byte[] derBytes = Convert.FromBase64String(base64);

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(derBytes, out _);

            RSAParameters rsaParams = rsa.ExportParameters(false);

            static string Base64UrlEncode(byte[] data) =>
                Base64UrlEncoder.Encode(data);

            var jwk = new JsonWebKey
            {
                Kty = "RSA",
                N   = Base64UrlEncode(rsaParams.Modulus!),
                E   = Base64UrlEncode(rsaParams.Exponent!),
                Use = "enc",
                Alg = "RSA-OAEP-256"
            };

            // Manually inject "ext": true
            jwk.AdditionalData["ext"] = true;

            return jwk;
        }

        public static RSA GetRsaPrivate(string privatePemPath)
        {
            var pem = File.ReadAllText(privatePemPath).Trim();
            const string pkcs1Header = "-----BEGIN RSA PRIVATE KEY-----";
            const string pkcs1Footer = "-----END RSA PRIVATE KEY-----";
            const string pkcs8Header = "-----BEGIN PRIVATE KEY-----";
            const string pkcs8Footer = "-----END PRIVATE KEY-----";

            if (pem.StartsWith(pkcs1Header) && pem.EndsWith(pkcs1Footer))
            {
                var b64 = pem
                    .Replace(pkcs1Header, "")
                    .Replace(pkcs1Footer, "")
                    .Replace("\r", "")
                    .Replace("\n", "")
                    .Trim();
                byte[] der = Convert.FromBase64String(b64);
                var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(der, out _);
                return rsa;
            }
            else if (pem.StartsWith(pkcs8Header) && pem.EndsWith(pkcs8Footer))
            {
                var b64 = pem
                    .Replace(pkcs8Header, "")
                    .Replace(pkcs8Footer, "")
                    .Replace("\r", "")
                    .Replace("\n", "")
                    .Trim();
                byte[] der = Convert.FromBase64String(b64);
                var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(der, out _);
                return rsa;
            }
            else
            {
                throw new InvalidOperationException("Unrecognized private key format.");
            }
        }
    }
}
