// ThirdParty wrapper for Microsoft's msquic library — same QUIC stack the
// Arch (C#) server uses via StirlingLabs.MsQuic. Mirroring at the transport
// layer makes the Arch ↔ Bevy ↔ Unreal server comparison fair (all three
// negotiate the same QUIC handshake, datagram framing, congestion control).
//
// Binaries are NOT vendored in this folder — see README.md for download
// + placement instructions. Build.cs fails fast at link time with a clear
// error if the expected files are missing.

using UnrealBuildTool;
using System.IO;

// Module renamed from "MsQuic" → "UvHSMsQuic" because Unreal has an internal
// MsQuicRuntime engine plugin / MsQuic module pair, and UBT rejects project
// modules whose names collide with engine-plugin modules (the cross-hierarchy
// reference rule). Same code, just a unique name.
public class UvHSMsQuic : ModuleRules
{
	public UvHSMsQuic(ReadOnlyTargetRules Target) : base(Target)
	{
		Type = ModuleType.External;

		string IncludeDir = Path.Combine(ModuleDirectory, "include");
		string LibDir     = Path.Combine(ModuleDirectory, "lib");

		PublicSystemIncludePaths.Add(IncludeDir);

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			string WinLibDir = Path.Combine(LibDir, "Win64");
			string WinLib = Path.Combine(WinLibDir, "msquic.lib");
			PublicAdditionalLibraries.Add(WinLib);

			// Ship msquic.dll plus any auxiliary DLLs the variant brings.
			// The MsQuic.OpenSSL3 NuGet may include libcrypto-3-x64.dll /
			// libssl-3-x64.dll alongside msquic.dll — drop_msquic.sh copies
			// every .dll from the NuGet's x64 dir, and we forward them all
			// to the binary output dir as RuntimeDependencies.
			if (Directory.Exists(WinLibDir))
			{
				foreach (string Dll in Directory.GetFiles(WinLibDir, "*.dll"))
				{
					string DllName = Path.GetFileName(Dll);
					RuntimeDependencies.Add("$(BinaryOutputDir)/" + DllName, Dll);
				}
			}
			PublicDelayLoadDLLs.Add("msquic.dll");
		}
		else if (Target.Platform == UnrealTargetPlatform.Linux ||
		         Target.Platform == UnrealTargetPlatform.LinuxArm64)
		{
			// Pick the right binary directory per architecture. UE 5 ships
			// LinuxArm64 as a first-class target for ARM-based microvms and
			// Ampere-class hardware (Hetzner ARM, Apple Silicon QEMU+hvf).
			//   lib/Linux/        → x86_64 glibc msquic (official openssl release)
			//   lib/LinuxArm64/   → aarch64 glibc msquic (official openssl release)
			// Both must export the same SONAME `libmsquic.so.2` so the runtime
			// loader resolves the dlopen call the same way.
			string LinuxSubDir = (Target.Platform == UnrealTargetPlatform.LinuxArm64) ? "LinuxArm64" : "Linux";
			string ArchLibDir  = Path.Combine(LibDir, LinuxSubDir);
			string LinSoVer    = Path.Combine(ArchLibDir, "libmsquic.so.2");

			if (!File.Exists(LinSoVer))
			{
				throw new BuildException(
					"MsQuic: missing " + LinSoVer + ".\n" +
					"        Download the matching aarch64 msquic openssl release from\n" +
					"        https://github.com/microsoft/msquic/releases (the\n" +
					"        msquic_linux_aarch64_openssl tarball) and drop the\n" +
					"        libmsquic.so.2 file into Source/ThirdParty/MsQuic/lib/LinuxArm64/.\n" +
					"        See README.md for details.");
			}

			// UBT's Linux toolchain converts a `.so.<N>` path into
			// `-l<basename-no-libprefix-no-version>` which lld can't resolve
			// (it would need `-l:libmsquic.so.2` to honor the literal name).
			// Workaround: ship the versioned copy via RuntimeDependencies and
			// dlopen it at runtime (RTLD_DEEPBIND) — avoids the link-time
			// resolve and also dodges the OpenSSL 1.1 symbol collision with
			// Unreal's statically linked OpenSSL.
			RuntimeDependencies.Add("$(BinaryOutputDir)/libmsquic.so.2", LinSoVer);
		}
		else
		{
			throw new BuildException("MsQuic: unsupported platform " + Target.Platform);
		}
	}
}
