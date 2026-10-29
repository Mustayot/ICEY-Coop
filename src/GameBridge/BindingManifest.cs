using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace IceyCoop.GameBridge
{
    public sealed class BindingSpec
    {
        public string TypeName;
        public string MemberName;
        public string[] ParameterTypeNames;
        public string ReturnTypeName;
        public bool IsStatic;
        public string Kind;
        public bool IsCritical;
        public bool HasPatch;
    }

    /// <summary>
    /// Data-driven binding list. Every critical entry is verified against the
    /// loaded Assembly-CSharp before patching; any mismatch fails closed.
    /// </summary>
    public sealed class BindingManifest
    {
        public const string ExpectedGameAssemblySha256 = "3969F0D5339CEF74A12E7C7B11932DB727EF1CEA5FD83D112DD1ACD718A998D7";

        private readonly List<BindingSpec> bindings = new List<BindingSpec>();

        public BindingManifest()
        {
            bindings.Add(new BindingSpec
            {
                TypeName = "SaveData",
                MemberName = "Save",
                ParameterTypeNames = new[] { "GameData", "System.Boolean" },
                ReturnTypeName = "System.Boolean",
                IsStatic = true,
                Kind = "Prefix",
                IsCritical = true,
                HasPatch = true
            });
            bindings.Add(new BindingSpec
            {
                TypeName = "SaveData",
                MemberName = "Delete",
                ParameterTypeNames = new[] { "System.Boolean" },
                ReturnTypeName = "System.Void",
                IsStatic = true,
                Kind = "Prefix",
                IsCritical = true,
                HasPatch = true
            });
        }

        public IList<BindingSpec> All
        {
            get { return bindings; }
        }

        /// <summary>
        /// Verifies that the currently loaded game assembly matches the audit
        /// baseline. Runs before any patch is applied.
        /// </summary>
        public void VerifyContentFingerprint()
        {
            string loc = typeof(R).Assembly.Location;
            string actual = ComputeSha256(loc);
            if (!string.Equals(actual, ExpectedGameAssemblySha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.Format("[icey-coop] Assembly-CSharp hash mismatch: expected {0} got {1} (file {2})",
                    ExpectedGameAssemblySha256, actual, loc));
            }
        }

        public static string ComputeSha256(string path)
        {
            using (SHA256Managed sha = new SHA256Managed())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }
    }
}
