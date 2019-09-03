// Copyright (C) 2011 Xamarin, Inc. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Xamarin.Android.Tools;
using Xamarin.Build;

namespace Xamarin.Android.Tasks
{
	public class Aapt : AsyncTask
	{
		public ITaskItem[] AdditionalAndroidResourcePaths { get; set; }

		public string AndroidComponentResgenFlagFile { get; set; }

		public bool NonConstantId { get; set; }

		public string AssetDirectory { get; set; }

		[Required]
		public ITaskItem[] ManifestFiles { get; set; }

		[Required]
		public string ResourceDirectory { get; set; }

		public string ResourceOutputFile { get; set; }

		[Required]
		public string JavaDesignerOutputDirectory { get; set; }

		[Required]
		public string JavaPlatformJarPath { get; set; }

		public string UncompressedFileExtensions { get; set; }
		public string PackageName { get; set; }

		[Required]
		public string ApplicationName { get; set; }

		public string ExtraPackages { get; set; }

		public ITaskItem [] AdditionalResourceDirectories { get; set; }

		public ITaskItem [] LibraryProjectJars { get; set; }

		public string ExtraArgs { get; set; }

		protected string ToolName { get { return OS.IsWindows ? "aapt.exe" : "aapt"; } }

		public string ToolPath { get; set; }

		public string ToolExe { get; set; }

		public string ApiLevel { get; set; }

		public bool AndroidUseLatestPlatformSdk { get; set; }

		public string [] SupportedAbis { get; set; }

		public bool CreatePackagePerAbi { get; set; }

		public string ImportsDirectory { get; set; }
		public string OutputImportDirectory { get; set; }
		public bool UseShortFileNames { get; set; }
		public string AssemblyIdentityMapFile { get; set; }

		public string ResourceNameCaseMap { get; set; }

		public bool ExplicitCrunch { get; set; }

		// pattern to use for the version code. Used in CreatePackagePerAbi
		// eg. {abi:00}{dd}{version}
		// known keyworks
		//  {abi} the value for the current abi
		//  {version} the version code from the manifest.
		public string VersionCodePattern { get; set; }

		// Name=Value pair seperated by ';'
		// e.g screen=21;abi=11
		public string VersionCodeProperties { get; set; }

		public string AndroidSdkPlatform { get; set; }

		public string ResourceSymbolsTextFileDirectory { get; set; }

		Dictionary<string,string> resource_name_case_map;
		AssemblyIdentityMap assemblyMap = new AssemblyIdentityMap ();
		string resourceDirectory;

		bool ManifestIsUpToDate (string manifestFile)
		{
			return !String.IsNullOrEmpty (AndroidComponentResgenFlagFile) &&
				File.Exists (AndroidComponentResgenFlagFile) && File.Exists (manifestFile) &&
				File.GetLastWriteTime (AndroidComponentResgenFlagFile) > File.GetLastWriteTime (manifestFile);
		}

		bool RunAapt (string commandLine, IList<OutputLine> output)
		{
			var stdout_completed = new ManualResetEvent (false);
			var stderr_completed = new ManualResetEvent (false);
			var psi = new ProcessStartInfo () {
				FileName = GenerateFullPathToTool (),
				Arguments = commandLine,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = WorkingDirectory,
			};
			object lockObject = new object ();
			using (var proc = new Process ()) {
				proc.OutputDataReceived += (sender, e) => {
					if (e.Data != null)
						lock (lockObject)
							output.Add (new OutputLine (e.Data, stdError: false));
					else
						stdout_completed.Set ();
				};
				proc.ErrorDataReceived += (sender, e) => {
					if (e.Data != null)
						lock (lockObject)
							output.Add (new OutputLine (e.Data, stdError: true));
					else
						stderr_completed.Set ();
				};
				proc.StartInfo = psi;
				LogDebugMessage ("Executing {0}", commandLine);
				proc.Start ();
				proc.BeginOutputReadLine ();
				proc.BeginErrorReadLine ();
				CancellationToken.Register (() => {
					try {
						proc.Kill ();
					} catch (Exception) {
					}
				});
				proc.WaitForExit ();
				if (psi.RedirectStandardError)
					stderr_completed.WaitOne (TimeSpan.FromSeconds (30));
				if (psi.RedirectStandardOutput)
					stdout_completed.WaitOne (TimeSpan.FromSeconds (30));
				return proc.ExitCode == 0;
			}
		}

		bool ExecuteForAbi (string cmd, string currentResourceOutputFile)
		{
			var output = new List<OutputLine> ();
			var ret = RunAapt (cmd, output);
			var success = !string.IsNullOrEmpty (currentResourceOutputFile)
				? File.Exists (Path.Combine (currentResourceOutputFile + ".bk"))
				: ret;
			foreach (var line in output) {
				if (line.StdError) {
					LogEventsFromTextOutput (line.Line, MessageImportance.Normal, success);
				} else {
					LogMessage (line.Line, MessageImportance.Normal);
				}
			}
			if (ret && !string.IsNullOrEmpty (currentResourceOutputFile)) {
				var tmpfile = currentResourceOutputFile + ".bk";
				MonoAndroidHelper.CopyIfZipChanged (tmpfile, currentResourceOutputFile);
				File.Delete (tmpfile);
			}
			return ret;
		}

		void ProcessManifest (ITaskItem manifestFile)
		{
			var manifest = Path.IsPathRooted (manifestFile.ItemSpec) ? manifestFile.ItemSpec : Path.Combine (WorkingDirectory, manifestFile.ItemSpec);
			if (!File.Exists (manifest)) {
				LogDebugMessage ("{0} does not exists. Skipping", manifest);
				return;
			}

			bool upToDate = ManifestIsUpToDate (manifest);

			if (AdditionalAndroidResourcePaths != null)
				foreach (var dir in AdditionalAndroidResourcePaths)
					if (!string.IsNullOrEmpty (dir.ItemSpec))
						upToDate = upToDate && ManifestIsUpToDate (string.Format ("{0}{1}{2}{3}{4}", dir, Path.DirectorySeparatorChar, "manifest", Path.DirectorySeparatorChar, "AndroidManifest.xml"));

			if (upToDate) {
				LogMessage ("  Additional Android Resources manifsets files are unchanged. Skipping.");
				return;
			}

			var defaultAbi = new string [] { null };
			var abis = CreatePackagePerAbi && SupportedAbis?.Length > 1 ? defaultAbi.Concat (SupportedAbis) : defaultAbi;
			foreach (var abi in abis) {
				var currentResourceOutputFile = abi != null ? string.Format ("{0}-{1}", ResourceOutputFile, abi) : ResourceOutputFile;
				if (!string.IsNullOrEmpty (currentResourceOutputFile) && !Path.IsPathRooted (currentResourceOutputFile))
					currentResourceOutputFile = Path.Combine (WorkingDirectory, currentResourceOutputFile);
				string cmd = GenerateCommandLineCommands (manifest, abi, currentResourceOutputFile);
				if (string.IsNullOrWhiteSpace (cmd) || !ExecuteForAbi (cmd, currentResourceOutputFile)) {
					Cancel ();
				}
			}

			return;
		}

		public override bool Execute () 
		{
			resourceDirectory = ResourceDirectory.TrimEnd ('\\');
			if (!Path.IsPathRooted (resourceDirectory))
				resourceDirectory = Path.Combine (WorkingDirectory, resourceDirectory);
			Yield ();
			try {
				var task = this.RunTask (DoExecute);

				task.ContinueWith (Complete);

				base.Execute ();
			} finally {
				Reacquire ();
			}

			return !Log.HasLoggedErrors;
		}

		void DoExecute ()
		{
			resource_name_case_map = MonoAndroidHelper.LoadResourceCaseMap (ResourceNameCaseMap);

			assemblyMap.Load (Path.Combine (WorkingDirectory, AssemblyIdentityMapFile));

			this.ParallelForEach (ManifestFiles, ProcessManifest);
		}

		protected string GenerateCommandLineCommands (string ManifestFile, string currentAbi, string currentResourceOutputFile)
		{
			// For creating Resource.designer.cs:
			//   Running command: C:\Program Files (x86)\Android\android-sdk-windows\platform-tools\aapt
			//     "package"
			//     "-M" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way\AndroidManifest.xml"
			//     "-J" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way"
			//     "-F" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way\resources.apk"
			//     "-S" "c:\users\jonathan\documents\visual studio 2010\Projects\MonoAndroidApplication4\MonoAndroidApplication4\obj\Debug\res"
			//     "-I" "C:\Program Files (x86)\Android\android-sdk-windows\platforms\android-8\android.jar"
			//     "--max-res-version" "10"

			// For packaging:
			//   Running command: C:\Program Files (x86)\Android\android-sdk-windows\platform-tools\aapt
			//     "package"
			//     "-f"
			//     "-m"
			//     "-M" "AndroidManifest.xml"
			//     "-J" "src"
			//     "--custom-package" "androidmsbuildtest.androidmsbuildtest"
			//     "-F" "bin\packaged_resources"
			//     "-S" "C:\Users\Jonathan\Documents\Visual Studio 2010\Projects\AndroidMSBuildTest\AndroidMSBuildTest\obj\Debug\res"
			//     "-I" "C:\Program Files (x86)\Android\android-sdk-windows\platforms\android-8\android.jar"
			//     "--extra-packages" "com.facebook.android:my.another.library"

			var cmd = new CommandLineBuilder ();

			cmd.AppendSwitch ("package");

			if (MonoAndroidHelper.LogInternalExceptions)
				cmd.AppendSwitch ("-v");
			if (NonConstantId)
				cmd.AppendSwitch ("--non-constant-id");
			cmd.AppendSwitch ("-f");
			cmd.AppendSwitch ("-m");
			string manifestFile;
			string manifestDir = Path.Combine (Path.GetDirectoryName (ManifestFile), currentAbi != null ? currentAbi : "manifest");

			Directory.CreateDirectory (manifestDir);
			manifestFile = Path.Combine (manifestDir, Path.GetFileName (ManifestFile));
			ManifestDocument manifest = new ManifestDocument (ManifestFile, this.Log);
			manifest.SdkVersion = AndroidSdkPlatform;
			if (!string.IsNullOrEmpty (VersionCodePattern)) {
				try {
					manifest.CalculateVersionCode (currentAbi, VersionCodePattern, VersionCodeProperties);
				} catch (ArgumentOutOfRangeException ex) {
					Log.LogCodedError ("XA0003", ManifestFile, 0, ex.Message);
					return string.Empty;
				}
			}
			if (currentAbi != null && string.IsNullOrEmpty (VersionCodePattern)) {
				manifest.SetAbi (currentAbi);
			}
			if (!manifest.ValidateVersionCode (out string error, out string errorCode)) {
				Log.LogCodedError (errorCode, ManifestFile, 0, error);
				return string.Empty;
			}
			manifest.ApplicationName = ApplicationName;
			manifest.Save (manifestFile);

			cmd.AppendSwitchIfNotNull ("-M ", manifestFile);
			var designerDirectory = Path.IsPathRooted (JavaDesignerOutputDirectory) ? JavaDesignerOutputDirectory : Path.Combine (WorkingDirectory, JavaDesignerOutputDirectory);
			Directory.CreateDirectory (designerDirectory);
			cmd.AppendSwitchIfNotNull ("-J ", JavaDesignerOutputDirectory);

			if (PackageName != null)
				cmd.AppendSwitchIfNotNull ("--custom-package ", PackageName.ToLowerInvariant ());

			if (!string.IsNullOrEmpty (currentResourceOutputFile))
				cmd.AppendSwitchIfNotNull ("-F ", currentResourceOutputFile + ".bk");
			// The order of -S arguments is *important*, always make sure this one comes FIRST
			cmd.AppendSwitchIfNotNull ("-S ", resourceDirectory.TrimEnd ('\\'));
			if (AdditionalResourceDirectories != null) {
				foreach (var dir in AdditionalResourceDirectories) {
					var resdir = dir.ItemSpec.TrimEnd ('\\');
					if (Directory.Exists (resdir)) {
						cmd.AppendSwitchIfNotNull ("-S ", resdir);
					}
				}
			}
			if (AdditionalAndroidResourcePaths != null) {
				foreach (var dir in AdditionalAndroidResourcePaths) {
					var resdir = Path.Combine (dir.ItemSpec, "res");
					if (Directory.Exists (resdir)) {
						cmd.AppendSwitchIfNotNull ("-S ", resdir);
					}
				}
			}

			if (LibraryProjectJars != null)
				foreach (var jar in LibraryProjectJars)
					cmd.AppendSwitchIfNotNull ("-j ", jar);
			
			cmd.AppendSwitchIfNotNull ("-I ", JavaPlatformJarPath);

			// Add asset directory if it exists
			if (!string.IsNullOrWhiteSpace (AssetDirectory)) {
				var assetDir = AssetDirectory.TrimEnd ('\\');
				if (!Path.IsPathRooted (assetDir))
					assetDir = Path.Combine (WorkingDirectory, assetDir);
				if (!string.IsNullOrWhiteSpace (assetDir) && Directory.Exists (assetDir))
					cmd.AppendSwitchIfNotNull ("-A ", assetDir);
			}
			if (!string.IsNullOrWhiteSpace (UncompressedFileExtensions))
				foreach (var ext in UncompressedFileExtensions.Split (new char[] { ';', ','}, StringSplitOptions.RemoveEmptyEntries))
					cmd.AppendSwitchIfNotNull ("-0 ", ext);

			if (!string.IsNullOrEmpty (ExtraPackages))
				cmd.AppendSwitchIfNotNull ("--extra-packages ", ExtraPackages);

			// TODO: handle resource names
			if (ExplicitCrunch)
				cmd.AppendSwitch ("--no-crunch");

			cmd.AppendSwitch ("--auto-add-overlay");

			if (!string.IsNullOrEmpty (ResourceSymbolsTextFileDirectory))
				cmd.AppendSwitchIfNotNull ("--output-text-symbols ", ResourceSymbolsTextFileDirectory);

			var extraArgsExpanded = ExpandString (ExtraArgs);
			if (extraArgsExpanded != ExtraArgs)
				Log.LogDebugMessage ("  ExtraArgs expanded: {0}", extraArgsExpanded);

			if (!string.IsNullOrWhiteSpace (extraArgsExpanded))
				cmd.AppendSwitch (extraArgsExpanded);

			if (!AndroidUseLatestPlatformSdk)
				cmd.AppendSwitchIfNotNull ("--max-res-version ", ApiLevel);

			return cmd.ToString ();
		}

		string ExpandString (string s)
		{
			if (s == null)
				return null;
			int start = 0;
			int st = s.IndexOf ("${library.imports:", start, StringComparison.Ordinal);
			if (st >= 0) {
				int ed = s.IndexOf ('}', st);
				if (ed < 0)
					return s.Substring (0, st + 1) + ExpandString (s.Substring (st + 1));
				int ast = st + "${library.imports:".Length;
				string aname = s.Substring (ast, ed - ast);
				return s.Substring (0, st) + Path.Combine (OutputImportDirectory, UseShortFileNames ? assemblyMap.GetLibraryImportDirectoryNameForAssembly (aname) : aname, ImportsDirectory) + Path.DirectorySeparatorChar + ExpandString (s.Substring (ed + 1));
			}
			else
				return s;
		}

		protected string GenerateFullPathToTool ()
		{
			return Path.Combine (ToolPath, string.IsNullOrEmpty (ToolExe) ? ToolName : ToolExe);
		}

		protected void LogEventsFromTextOutput (string singleLine, MessageImportance messageImportance, bool apptResult)
		{
			if (string.IsNullOrEmpty (singleLine)) 
				return;

			var match = AndroidToolTask.AndroidErrorRegex.Match (singleLine.Trim ());

			if (match.Success) {
				var file = match.Groups["file"].Value;
				int line = 0;
				if (!string.IsNullOrEmpty (match.Groups["line"]?.Value))
					line = int.Parse (match.Groups["line"].Value.Trim ()) + 1;
				var level = match.Groups["level"].Value.ToLowerInvariant ();
				var message = match.Groups ["message"].Value;
				if (message.Contains ("fakeLogOpen")) {
					LogMessage (singleLine, MessageImportance.Normal);
					return;
				}
				if (level.Contains ("warning")) {
					LogCodedWarning (GetErrorCode (singleLine), singleLine);
					return;
				}

				// Try to map back to the original resource file, so when the user
				// double clicks the error, it won't take them to the obj/Debug copy
				string newfile = MonoAndroidHelper.FixUpAndroidResourcePath (file, resourceDirectory, string.Empty, resource_name_case_map);
				if (!string.IsNullOrEmpty (newfile)) {
					file = newfile;
				}

				// Strip any "Error:" text from aapt's output
				if (message.StartsWith ("error: ", StringComparison.InvariantCultureIgnoreCase))
					message = message.Substring ("error: ".Length);

				if (level.Contains ("error") || (line != 0 && !string.IsNullOrEmpty (file))) {
					LogCodedError (GetErrorCode (message), message, file, line);
					return;
				}
			}

			if (!apptResult) {
				var message = string.Format ("{0} \"{1}\".", singleLine.Trim (), singleLine.Substring (singleLine.LastIndexOfAny (new char [] { '\\', '/' }) + 1));
				LogCodedError (GetErrorCode (message), message, ToolName);
			} else {
				LogCodedWarning (GetErrorCode (singleLine), singleLine);
			}
		}

		static string GetErrorCode (string message)
		{
			foreach (var tuple in error_codes)
				if (message.IndexOf (tuple.Item1, StringComparison.OrdinalIgnoreCase) >= 0)
					return tuple.Item2;

			return "APT0000";
		}

		static readonly List<Tuple<string, string>> error_codes = new List<Tuple<string, string>> () {
			Tuple.Create ("AndroidManifest.xml is corrupt", "APT1100"),
			Tuple.Create ("can't use '-u' with add", "APT1001"),
			Tuple.Create ("dump failed because assets could not be loaded", "APT1002"),
			Tuple.Create ("dump failed because no AndroidManifest.xml found", "APT1003"),
			Tuple.Create ("dump failed because the resource table is invalid/corrupt", "APT1004"),
			Tuple.Create ("during crunch - archive is toast", "APT1005"),
			Tuple.Create ("failed to get platform version code", "APT1006"),
			Tuple.Create ("failed to get platform version name", "APT1007"),
			Tuple.Create ("failed to get XML element name (bad string pool)", "APT1008"),
			Tuple.Create ("failed to write library table", "APT1009"),
			Tuple.Create ("getting resolved resource attribute", "APT1010"),
			Tuple.Create ("Key string data is corrupt", "APT1011"),
			Tuple.Create ("list -a failed because assets could not be loaded", "APT1012"),
			Tuple.Create ("manifest does not start with <manifest> tag", "APT1013"),
			Tuple.Create ("missing 'android:name' for permission", "APT1014"),
			Tuple.Create ("missing 'android:name' for uses-permission", "APT1015"),
			Tuple.Create ("missing 'android:name' for uses-permission-sdk-23", "APT1016"),
			Tuple.Create ("Missing entries, quit", "APT1017"),
			Tuple.Create ("must specify zip file name", "APT1018"),
			Tuple.Create ("No AndroidManifest.xml file found", "APT1019"),
			Tuple.Create ("No argument supplied for '-A' option", "APT1020"),
			Tuple.Create ("No argument supplied for '-c' option", "APT1021"),
			Tuple.Create ("No argument supplied for '--custom-package' option", "APT1022"),
			Tuple.Create ("No argument supplied for '-D' option", "APT1023"),
			Tuple.Create ("No argument supplied for '-e' option", "APT1024"),
			Tuple.Create ("No argument supplied for '--extra-packages' option", "APT1025"),
			Tuple.Create ("No argument supplied for '--feature-after' option", "APT1026"),
			Tuple.Create ("No argument supplied for '--feature-of' option", "APT1027"),
			Tuple.Create ("No argument supplied for '-F' option", "APT1028"),
			Tuple.Create ("No argument supplied for '-g' option", "APT1029"),
			Tuple.Create ("No argument supplied for '--ignore-assets' option", "APT1030"),
			Tuple.Create ("No argument supplied for '-I' option", "APT1031"),
			Tuple.Create ("No argument supplied for '-j' option", "APT1032"),
			Tuple.Create ("No argument supplied for '--max-res-version' option", "APT1033"),
			Tuple.Create ("No argument supplied for '--max-sdk-version' option", "APT1034"),
			Tuple.Create ("No argument supplied for '--min-sdk-version' option", "APT1035"),
			Tuple.Create ("No argument supplied for '-M' option", "APT1036"),
			Tuple.Create ("No argument supplied for '-o' option", "APT1037"),
			Tuple.Create ("No argument supplied for '-output-text-symbols' option", "APT1038"),
			Tuple.Create ("No argument supplied for '-P' option", "APT1039"),
			Tuple.Create ("No argument supplied for '--preferred-density' option", "APT1040"),
			Tuple.Create ("No argument supplied for '--private-symbols' option", "APT1041"),
			Tuple.Create ("No argument supplied for '--product' option", "APT1042"),
			Tuple.Create ("No argument supplied for '--rename-instrumentation-target-package' option", "APT1043"),
			Tuple.Create ("No argument supplied for '--rename-manifest-package' option", "APT1044"),
			Tuple.Create ("No argument supplied for '-S' option", "APT1045"),
			Tuple.Create ("No argument supplied for '--split' option", "APT1046"),
			Tuple.Create ("No argument supplied for '--target-sdk-version' option", "APT1047"),
			Tuple.Create ("No argument supplied for '--version-code' option", "APT1048"),
			Tuple.Create ("No argument supplied for '--version-name' option", "APT1049"),
			Tuple.Create ("no dump file specified", "APT1050"),
			Tuple.Create ("no dump option specified", "APT1051"),
			Tuple.Create ("no dump xmltree resource file specified", "APT1052"),
			Tuple.Create ("no input files", "APT1053"),
			Tuple.Create ("no <manifest> tag found in platform AndroidManifest.xml", "APT1054"),
			Tuple.Create ("out of memory creating package chunk for ResTable_header", "APT1055"),
			Tuple.Create ("out of memory creating ResTable_entry", "APT1056"),
			Tuple.Create ("out of memory creating ResTable_header", "APT1057"),
			Tuple.Create ("out of memory creating ResTable_package", "APT1058"),
			Tuple.Create ("out of memory creating ResTable_type", "APT1059"),
			Tuple.Create ("out of memory creating ResTable_typeSpec", "APT1060"),
			Tuple.Create ("out of memory creating Res_value", "APT1061"),
			Tuple.Create ("Out of memory for string pool", "APT1062"),
			Tuple.Create ("Out of memory padding string pool", "APT1063"),
			Tuple.Create ("parsing XML", "APT1064"),
			Tuple.Create ("Platform AndroidManifest.xml is corrupt", "APT1065"),
			Tuple.Create ("Platform AndroidManifest.xml not found", "APT1066"),
			Tuple.Create ("print resolved resource attribute", "APT1067"),
			Tuple.Create ("retrieving parent for item:", "APT1068"),
			Tuple.Create ("specify zip file name (only)", "APT1069"),
			Tuple.Create ("Type string data is corrupt", "APT1070"),
			Tuple.Create ("Unable to parse generated resources, aborting", "APT1071"),
			Tuple.Create ("Invalid BCP 47 tag in directory name", "APT1072"),	// ERROR: Invalid BCP 47 tag in directory name: %s
			Tuple.Create ("parsing preferred density", "APT1078"),			// Error parsing preferred density: %s
			Tuple.Create ("Asset package include", "APT1079"),			// ERROR: Asset package include '%s' not found
			Tuple.Create ("base feature package", "APT1080"),			// ERROR: base feature package '%s' not found
			Tuple.Create ("Split configuration", "APT1081"),			// ERROR: Split configuration '%s' is already defined in another split
			Tuple.Create ("failed opening/creating", "APT1082"),			// ERROR: failed opening/creating '%s' as Zip file
			Tuple.Create ("as Zip file for writing", "APT1083"),			// ERROR: unable to open '%s' as Zip file for writing
			Tuple.Create ("as Zip file", "APT1084"),				// ERROR: failed opening '%s' as Zip file
			Tuple.Create ("included asset path", "APT1085"),			// ERROR: included asset path %s could not be loaded
			Tuple.Create ("getting 'android:name' attribute", "APT1086"),
			Tuple.Create ("getting 'android:name'", "APT1087"),
			Tuple.Create ("getting 'android:versionCode' attribute", "APT1088"),
			Tuple.Create ("getting 'android:versionName' attribute", "APT1089"),
			Tuple.Create ("getting 'android:compileSdkVersion' attribute", "APT1090"),
			Tuple.Create ("getting 'android:installLocation' attribute", "APT1091"),
			Tuple.Create ("getting 'android:icon' attribute", "APT1092"),
			Tuple.Create ("getting 'android:testOnly' attribute", "APT1093"),
			Tuple.Create ("getting 'android:banner' attribute", "APT1094"),
			Tuple.Create ("getting 'android:isGame' attribute", "APT1095"),
			Tuple.Create ("getting 'android:debuggable' attribute", "APT1096"),
			Tuple.Create ("getting 'android:minSdkVersion' attribute", "APT1097"),
			Tuple.Create ("getting 'android:targetSdkVersion' attribute", "APT1098"),
			Tuple.Create ("getting 'android:label' attribute", "APT1099"),
			Tuple.Create ("getting compatible screens", "APT1100"),
			Tuple.Create ("getting 'android:name' attribute for uses-library", "APT1101"),
			Tuple.Create ("getting 'android:name' attribute for receiver", "APT1102"),
			Tuple.Create ("getting 'android:permission' attribute for receiver", "APT1103"),
			Tuple.Create ("getting 'android:name' attribute for service", "APT1104"),
			Tuple.Create ("getting 'android:name' attribute for meta-data tag in service", "APT1105"),
			Tuple.Create ("getting 'android:name' attribute for meta-data", "APT1106"),
			Tuple.Create ("getting 'android:permission' attribute for service", "APT1107"),
			Tuple.Create ("getting 'android:permission' attribute for provider", "APT1108"),
			Tuple.Create ("getting 'android:exported' attribute for provider", "APT1109"),
			Tuple.Create ("getting 'android:grantUriPermissions' attribute for provider", "APT1110"),
			Tuple.Create ("getting 'android:value' or 'android:resource' attribute for meta-data", "APT1111"),
			Tuple.Create ("getting 'android:resource' attribute for meta-data tag in service", "APT1112"),
			Tuple.Create ("getting AID category for service", "APT1113"),
			Tuple.Create ("getting 'name' attribute", "APT1114"),
			Tuple.Create ("unknown dump option", "APT1115"),
			Tuple.Create ("failed opening Zip archive", "APT1116"),
			Tuple.Create ("exists but is not regular file", "APT1117"),		// ERROR: output file '%s' exists but is not regular file
			Tuple.Create ("failed to parse split configuration", "APT1118"),
			Tuple.Create ("packaging of", "APT1119"),				// ERROR: packaging of '%s' failed
			Tuple.Create ("9-patch image", "APT1120"),				// ERROR: 9-patch image %s malformed
			Tuple.Create ("Failure processing PNG image", "APT1121"),
			Tuple.Create ("Unknown command", "APT1122"),
			Tuple.Create ("exists (use '-f' to force overwrite)", "APT1123"),
			Tuple.Create ("exists and is not a regular file", "APT1124"),
			Tuple.Create ("unable to process assets while packaging", "APT1125"),
			Tuple.Create ("unable to process jar files while packaging", "APT1126"),
			Tuple.Create ("Unknown option", "APT1127"),
			Tuple.Create ("Unknown flag", "APT1128"),
			Tuple.Create ("Zip flush failed, archive may be hosed", "APT1129"),
			Tuple.Create ("exists twice (check for with", "APT1130"),		// ERROR: '%s' exists twice (check for with & w/o '.gz'?)
			Tuple.Create ("unable to uncompress entry", "APT1131"),
			Tuple.Create ("as a zip file", "APT1132"),				// ERROR: unable to open '%s' as a zip file: %d
			Tuple.Create ("unable to process", "APT1133"),				// ERROR: unable to process '%s'
			Tuple.Create ("malformed resource filename", "APT1134"),
			Tuple.Create ("AndroidManifest.xml already defines", "APT1135"),	// Error: AndroidManifest.xml already defines %s (in %s); cannot insert new value %s
			Tuple.Create ("In <declare-styleable>", "APT1136"),			// ERROR: In <declare-styleable> %s, unable to find attribute %s
			Tuple.Create ("Feature package", "APT1137"),				// ERROR: Feature package '%s' not found
			Tuple.Create ("declaring public resource", "APT1138"),			// Error declaring public resource %s/%s for included package %s
			Tuple.Create ("with value", "APT1139"),					// Error: %s (at '%s' with value '%s')
			Tuple.Create ("is not a single item or a bag", "APT1140"),		// Error: entry %s is not a single item or a bag
			Tuple.Create ("adding span for style tag", "APT1141"),
			Tuple.Create ("parsing XML", "APT1142"),
			Tuple.Create ("access denied", "APT1143"),				// ERROR: '%s' access denied
			Tuple.Create ("included asset path", "APT1144"),			// ERROR: included asset path %s could not be loaded
			Tuple.Create ("is corrupt", "APT1145"),					// ERROR: Resource %s is corrupt
			Tuple.Create ("dump failed because resource", "APT1146"),		// ERROR: dump failed because resource %s [not] found
			Tuple.Create ("not found", "APT1147"),					// ERROR: '%s' not found
			Tuple.Create ("asset directory", "APT1073"),				// ERROR: asset directory '%s' does not exist
			Tuple.Create ("input directory", "APT1074"),				// ERROR: input directory '%s' does not exist
			Tuple.Create ("resource directory", "APT1075"),				// ERROR: resource directory '%s' does not exist
			Tuple.Create ("is not a directory", "APT1076"),				// ERROR: '%s' is not a directory
			Tuple.Create ("opening zip file", "APT1077"),				// error opening zip file %s
		};
	}
}
