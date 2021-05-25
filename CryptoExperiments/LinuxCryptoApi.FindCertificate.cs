﻿namespace CryptoExperiments
{
    using System;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography.X509Certificates;
    using CryptoExperiments.Corefx.Common.Interop.Linux;

    public partial class LinuxCryptoApi
    {
        public unsafe X509Certificate2? FindBySubjectName(string subjectName)
        {
            fixed (char* pSubjectName = subjectName)
            {
                return FindCore<object>(Interop.CertFindType.CERT_FIND_SUBJECT_STR, pSubjectName);
            }
        }

        public unsafe X509Certificate2? FindByIssuerName(string issuerName)
        {
            fixed (char* pIssuerName = issuerName)
            {
                return FindCore<object>(Interop.CertFindType.CERT_FIND_ISSUER_STR, pIssuerName);
            }
        }

        public unsafe X509Certificate2? FindBySerialNumber(BigInteger hexValue, BigInteger decimalValue)
        {
            return FindCore(
                (hexValue, decimalValue),
                static (state, pCertContext) =>
                {
                    byte[] actual = ToByteArray(pCertContext.CertContext->pCertInfo->SerialNumber);
                    GC.KeepAlive(pCertContext);

                    // Convert to BigInteger as the comparison must not fail due to spurious leading zeros
                    BigInteger actualAsBigInteger = PositiveBigIntegerFromByteArray(actual);

                    return state.hexValue.Equals(actualAsBigInteger) || state.decimalValue.Equals(actualAsBigInteger);
                });
        }

        public unsafe X509Certificate2? FindByThumbprint(byte[] thumbPrint)
        {
            fixed (byte* pThumbPrint = thumbPrint)
            {
                var blob = new Interop.CRYPTOAPI_BLOB(thumbPrint.Length, pThumbPrint);
                return FindCore<object>(Interop.CertFindType.CERT_FIND_HASH, &blob);
            }
        }

        private unsafe X509Certificate2? FindCore<TState>(
            TState state,
            Func<TState, Interop.SafeCertContextHandle, bool> filter)
        {
            return this.FindCore(Interop.CertFindType.CERT_FIND_ANY, null, state, filter);
        }

        private unsafe X509Certificate2? FindCore<TState>(
            Interop.CertFindType dwFindType,
            void* pvFindPara,
            TState state = default!,
            Func<TState, Interop.SafeCertContextHandle, bool>? filter = null,
            bool validOnly = false,
            StoreLocation storeLocation = StoreLocation.CurrentUser,
            StoreName storeName = StoreName.My)
        {
            var certStore = storeLocation switch
            {
                StoreLocation.CurrentUser => Interop.CertStoreFlags.CERT_SYSTEM_STORE_CURRENT_USER,
                StoreLocation.LocalMachine => Interop.CertStoreFlags.CERT_SYSTEM_STORE_LOCAL_MACHINE,
                _ => throw new ArgumentOutOfRangeException(nameof(storeLocation), storeLocation, null),
            };

            using (var store = Interop.Libcapi20.CertOpenStore(
                Interop.CertStoreProvider.CERT_STORE_PROV_SYSTEM_A,
                Interop.CertEncodingType.All,
                IntPtr.Zero,
                certStore,
                storeName.ToString("G")))
            {
                if (store.IsInvalid)
                {
                    throw Marshal.GetLastWin32Error().ToCryptographicException();
                }

                Interop.SafeCertContextHandle? pCertContext = null;
                while (Interop.Libcapi20.CertFindCertificateInStore(store, dwFindType, pvFindPara, ref pCertContext))
                {
                    if (filter != null && !filter(state, pCertContext))
                        continue;

                    var contextCert = Marshal.PtrToStructure<Interop.Libcapi20.CERT_CONTEXT>(pCertContext.DangerousGetHandle());
                    var ctx = new byte[contextCert.cbCertEncoded];
                    Marshal.Copy((IntPtr)contextCert.pbCertEncoded, ctx, 0, ctx.Length);

                    var certificate = new X509Certificate2(ctx);

                    if (validOnly && !certificate.Verify())
                    {
                            continue;
                    }

                    return certificate;
                }
            }

            return null;
        }

        private static BigInteger PositiveBigIntegerFromByteArray(byte[] bytes)
        {
            // To prevent the big integer from misinterpreted as a negative number,
            // add a "leading 0" to the byte array if it would considered negative.
            //
            // Since BigInteger(bytes[]) requires a little-endian byte array,
            // the "leading 0" actually goes at the end of the array.

            // An empty array is 0 (non-negative), so no over-allocation is required.
            //
            // If the last indexed value doesn't have the sign bit set (0x00-0x7F) then
            // the number would be positive anyways, so no over-allocation is required.
            if (bytes.Length == 0 || bytes[^1] < 0x80)
            {
                return new BigInteger(bytes);
            }

            // Since the sign bit is set, put a new 0x00 on the end to move that bit from
            // the sign bit to a data bit.
            byte[] newBytes = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, newBytes, 0, bytes.Length);
            return new BigInteger(newBytes);
        }

        private static byte[] ToByteArray(Interop.Libcapi20.DATA_BLOB blob)
        {
            if (blob.cbData == 0)
            {
                return Array.Empty<byte>();
            }

            var length = (int)blob.cbData;
            byte[] data = new byte[length];
            Marshal.Copy(blob.pbData, data, 0, length);
            return data;
        }
    }
}