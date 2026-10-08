using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Neruna.Client.Tests;

/// <summary>A tiny PKI for tests: one CA and S/MIME user certificates issued by it.</summary>
internal static class TestPki
{
    private static readonly SecureRandom Random = new();
    private static readonly Lazy<(X509Certificate Certificate, AsymmetricCipherKeyPair Keys)> LazyCa = new(() =>
    {
        var keys = NewKeys();
        var name = new X509Name("CN=Neruna Test CA, O=Neruna");
        var generator = Generator(name, name, keys.Public, DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddYears(10));
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
        return (generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", keys.Private)), keys);
    });

    public static X509Certificate Ca => LazyCa.Value.Certificate;

    public static (X509Certificate Certificate, AsymmetricKeyParameter PrivateKey) User(string name, string email, DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var keys = NewKeys();
        var generator = Generator(Ca.SubjectDN, new X509Name($"CN={name}"), keys.Public, notBefore ?? DateTime.UtcNow.AddDays(-1), notAfter ?? DateTime.UtcNow.AddYears(2));
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation | KeyUsage.KeyEncipherment));
        generator.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.id_kp_emailProtection));
        generator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName(GeneralName.Rfc822Name, email)));
        return (generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", LazyCa.Value.Keys.Private)), keys.Private);
    }

    public static byte[] Pkcs12((X509Certificate Certificate, AsymmetricKeyParameter PrivateKey) user, string password)
    {
        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("neruna", new AsymmetricKeyEntry(user.PrivateKey), [new X509CertificateEntry(user.Certificate), new X509CertificateEntry(Ca)]);
        using var stream = new MemoryStream();
        store.Save(stream, password.ToCharArray(), Random);
        return stream.ToArray();
    }

    public static byte[] Pem(X509Certificate certificate)
    {
        using var writer = new StringWriter();
        using (var pem = new PemWriter(writer))
        {
            pem.WriteObject(certificate);
        }

        return System.Text.Encoding.ASCII.GetBytes(writer.ToString());
    }

    private static AsymmetricCipherKeyPair NewKeys()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(Random, 2048));
        return generator.GenerateKeyPair();
    }

    private static X509V3CertificateGenerator Generator(X509Name issuer, X509Name subject, AsymmetricKeyParameter publicKey, DateTime notBefore, DateTime notAfter)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, Random));
        generator.SetIssuerDN(issuer);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(notBefore);
        generator.SetNotAfter(notAfter);
        generator.SetPublicKey(publicKey);
        return generator;
    }
}
