﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using XCOM2Launcher.Helper;
using FilePath = System.IO.Path;

namespace XCOM2Launcher.Mod
{
    public class ModEntry
    {
        [JsonIgnore] private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(nameof(ModEntry));

        [JsonIgnore] private string _image;

        [JsonIgnore] private IEnumerable<ModClassOverride> _overrides;

        /// <summary>
        ///     Index to determine mod load order
        /// </summary>
        [DefaultValue(-1)]
        public int Index { get; set; } = -1;

        [JsonIgnore]
        public ModState State { get; private set; } = ModState.None;

        public string ID { get; set; }
        public string Name { get; set; }
        public bool ManualName { get; set; } = false;

        public string Author { get; set; } = "Unknown";
	    public string Description { get; set; } = "";

	    public string Path { get; set; } = "";

        /// <summary>
        ///     Size in bytes
        /// </summary>
        [DefaultValue(-1)]
        public long Size { get; set; } = -1;

        public bool isActive { get; set; } = false;
        public bool isHidden { get; set; } = false;

        public ModSource Source { get; set; } = ModSource.Unknown;

        [DefaultValue(-1)]
        public long WorkshopID { get; set; } = -1;

        public DateTime? DateAdded { get; set; } = null;
        public DateTime? DateCreated { get; set; } = null;
        public DateTime? DateUpdated { get; set; } = null;

        public string Note { get; set; } = null;

		[JsonIgnore]
	    public bool HasBackedUpSettings => Settings.Count > 0;

	    public List<ModSettingsEntry> Settings { get; set; } = new List<ModSettingsEntry>();

        [JsonIgnore]
        public string Image
        {
            get { return _image ?? FilePath.Combine(Path ?? "", "ModPreview.jpg"); }
            set { _image = value; }
        }

	    [JsonIgnore]
	    public string SteamLink => GetSteamLink();

	    [JsonIgnore]
	    public string BrowserLink => GetWorkshopLink();
        
        [Browsable(false)]
        public IList<string> Tags { get; set; } = new List<string>();

        public bool BuiltForWOTC { get; set; } = false;

        public Classes.Mod.ModProperty GetProperty()
        {
            return new Classes.Mod.ModProperty(this);
        }

		#region Mod

		public string GetDescription(bool CleanBBCode = false)
		{
            string dsc;
            if (!string.IsNullOrEmpty(Description))
                dsc = Description;
            else
                dsc = new ModInfo(GetModInfoFile()).Description;

            if (CleanBBCode)
            {
                dsc = dsc.Replace(@"\", @"\'5c");
                dsc = dsc.Replace(@"{", @"\'7b");
                dsc = dsc.Replace(@"}", @"\'7d");
                Regex Regexp = new Regex(@"(?<!\\\\)\[(/?)(.*?)(?<!\\\\)\]");
                dsc = Regexp.Replace(dsc, RTFEvaluator);
                Regex replace_linebreaks = new Regex(@"[\r\n]{1,2}");
                dsc = replace_linebreaks.Replace(dsc, @"\line ");
                return @"{\rtf1\ansi " + dsc + "}";
            }
            return Description;
        }

        private string RTFEvaluator(Match match)
        {
            String output;
            if (match.Groups[2].Value.StartsWith("url"))
            {
                if (match.Groups[1].Value == "/")
                {
                    return @"}}}";
                }
                else if (match.Groups[2].Value.Length > 3 && match.Groups[2].Value[3] == '=')
                {
                    return @"{\field{\*\fldinst{HYPERLINK """ + match.Groups[2].Value.Substring(4) + @"""}}{\fldrslt{\ul ";
                }
                else
                {
                    return @"{{{";
                }
            }
            else
            {
                switch (match.Groups[2].Value)
                {
                    case "h1":
                        output = @"{\fs20 \b ";
                        break;
                    case "strike":
                        output = @"{\strike ";
                        break;
                    case "b":
                        output = @"{\b ";
                        break;
                    case "i":
                        output = @"{\i ";
                        break;
                    case "u":
                        output = @"{\ul ";
                        break;
                    default:
                        output = "";
                        break;
                }
            }
            if (output != "" && match.Groups[1].Value == "/")
                output = "}";
            return output;
        }

        public IEnumerable<ModClassOverride> GetOverrides(bool forceUpdate = false)
        {
            if (_overrides == null || forceUpdate)
            {
                _overrides = GetUIScreenListenerOverrides().Union(GetClassOverrides()).ToList();
            }
            return _overrides;
        }

        private IEnumerable<ModClassOverride> GetUIScreenListenerOverrides()
        {
            var sourceDirectory = FilePath.Combine(Path, "Src");
            var overrides = new List<ModClassOverride>();

            if (!Directory.Exists(sourceDirectory))
            {
                return overrides;
            }


            string[] sourceFiles;

            try
            {
                sourceFiles = Directory.GetFiles(sourceDirectory, "*.uc", SearchOption.AllDirectories);
            }
            catch (IOException ex)
            {
                // We expect IOException because it happened in rare cases (Issue #122).
                Log.Error("Unable to get files from directory: " + sourceDirectory, ex);
                return overrides;
            }

            Parallel.ForEach(sourceFiles, sourceFile =>
            {
                //The XComGame directory usually contains ALL the source files for the game.  Leaving it in is a common mistake.
                if (sourceFile.IndexOf(@"Src\XComGame", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    return;
                }

                var screenClassRegex = new Regex(@"(?i)^\s*ScreenClass\s*=\s*(?:class')?([a-z_]+)");

                foreach (var line in File.ReadLines(sourceFile))
                {
                    var match = screenClassRegex.Match(line);
                    if (match.Success)
                    {
                        var oldClass = match.Groups[1].Value;
                        if (oldClass.ToLower() == "none")
                        {
                            //'ScreenClass = none' means it runs against every UI screen
                            continue;
                        }

                        var newClass = FilePath.GetFileNameWithoutExtension(sourceFile);
                        lock (overrides)
                        {
                            overrides.Add(new ModClassOverride(this, newClass, oldClass, ModClassOverrideType.UIScreenListener));
                        }
                    }
                }
            });

            return overrides;
        }

        private IEnumerable<ModClassOverride> GetClassOverrides()
        {
            var file = FilePath.Combine(Path, "Config", "XComEngine.ini");

            if (!File.Exists(file))
                return new ModClassOverride[0];

            var r = new Regex("^[+]?ModClassOverrides=\\(BaseGameClass=\"([^\"]+)\",ModClass=\"([^\"]+)\"\\)");

            return from line in File.ReadLines(file)
                select r.Match(line.Replace(" ", ""))
                into m
                where m.Success
                select new ModClassOverride(this, m.Groups[2].Value, m.Groups[1].Value, ModClassOverrideType.Class);
        }

        public void ShowOnSteam()
        {
            Tools.StartProcess("explorer", GetSteamLink());
        }

        public void ShowInBrowser()
        {
            Tools.StartProcess(GetWorkshopLink());
        }

        public void ShowInExplorer()
        {
            Tools.StartProcess("explorer", Path);
        }

        public string GetWorkshopLink()
        {
            if (WorkshopID > 0)
            {
                return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + WorkshopID;
            }
            return "";
        }

	    public string GetSteamLink()
	    {
            if (WorkshopID > 0)
            {
                return "steam://url/CommunityFilePage/" + WorkshopID;
            }
            return "";
	    }

        public String[] GetSteamTags()
        {
            if (WorkshopID > 0)
            {
                var details = Steam.Workshop.GetDetails((ulong) WorkshopID);
                return details.m_rgchTags.Split(',').Select(s => s.TrimStart(' ').TrimEnd(' ')).Where(s => !String.IsNullOrWhiteSpace(s)).ToArray();
            }
            return new String[0];
        }

        public override string ToString()
        {
            return ID;
        }

        public bool IsInModPath(string modPath)
        {
            return string.Equals(modPath.TrimEnd('/', '\\'), FilePath.GetDirectoryName(Path), StringComparison.OrdinalIgnoreCase);
        }

        #endregion Mod

        #region Public Setters
        public void SetState(ModState newState)
        {
            State = newState;
        }

        public void AddState(ModState newState)
        {
            State |= newState;
        }

        public void RemoveState(ModState newState)
        {
            State &= ~newState;
        }

        public void SetRequiresWOTC(bool NeedWOTC)
        {
            BuiltForWOTC = NeedWOTC;
        }

        public void RealizeSize(long newSize)
        {
            Size = newSize;
        }

        public void SetSource(ModSource newSource)
        {
            Source = newSource;
        }

        public void RealizeIDAndPath(string ModRoot)
        {
            Path = ModRoot;

            var info_file = Directory.GetFiles(Path, "*.XComMod", SearchOption.TopDirectoryOnly).SingleOrDefault();
            if (info_file != null)
            {
				ID = FilePath.GetFileNameWithoutExtension(info_file);
			}
        }
        #endregion


        #region Files

        public string[] GetConfigFiles()
        {
			if (Directory.Exists(FilePath.Combine(Path,"Config")))
				return Directory.GetFiles(FilePath.Combine(Path, "Config"), "*.ini", SearchOption.AllDirectories);
	        return new string[0];
        }

        internal string GetModInfoFile()
        {
            return FilePath.Combine(Path, ID + ".XComMod");
        }

        public string GetReadMe()
        {
            try
            {
                return File.ReadAllText(FilePath.Combine(Path, "ReadMe.txt"));
            }
            catch (IOException)
            {
                return "No ReadMe found.";
            }
        }

		/// <summary>
		/// Returns a relative path to this mod's folder.
		/// </summary>
		/// <param name="relativeTo"></param>
		/// <returns></returns>
		public string GetPathRelative(string relativeTo)
		{
			Uri modPath = new Uri(relativeTo);
			Uri filePath = new Uri(Path);
			var relativePath = filePath.MakeRelativeUri(modPath).ToString();

			// Trim off the mod ID number, it's not useful here
			int i = relativePath.IndexOf("Config", StringComparison.Ordinal);
			relativePath = relativePath.Substring(i);

			return Uri.UnescapeDataString(relativePath);
		}

		/// <summary>
		/// Returns the full path to the provided relative path.
		/// </summary>
		/// <param name="relativePath"></param>
		/// <returns></returns>
	    public string GetPathFull(string relativePath)
	    {
		    return FilePath.Combine(Path, relativePath);
	    }

		#endregion Files


		#region Settings

		public bool AddSetting(string path, string contents)
	    {
			if (Settings == null) Settings = new List<ModSettingsEntry>();
			// Check if this belongs to this mod
			var fullpath = GetPathFull(path);
			if (!File.Exists(fullpath))
			{
				MessageBox.Show(@"Error!\nThe file " + path + @" does not belong to mod " + Name + @".\nNothing was saved.", @"Error", MessageBoxButtons.OK);
				return false;
			}

			var setting = GetSetting(path);
			if (setting == null)
			{
				setting = new ModSettingsEntry(path, FilePath.GetFileName(path), contents);
				Settings.Add(setting);
			}
			else
				setting.Contents = contents;

            try
            {
                using (var stream = new StreamWriter(fullpath))
                {
                    stream.Write(contents);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warn("Error saving configuration file " + fullpath, ex);
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return true;

	    }

	    public bool RemoveSetting(string path)
	    {
		    ModSettingsEntry toRemove = Settings.FirstOrDefault(setting => setting.FilePath.Equals(path));

		    if (toRemove == null) return false;
		    Settings.Remove(toRemove);
		    return true;
	    }

		/// <summary>
		/// Returns a setting given a fully qualified path to a file
		/// </summary>
		/// <param name="path">Path to the setting to retreive</param>
		/// <returns></returns>
	    public ModSettingsEntry GetSetting(string path)
		{
			if (Settings == null) Settings = new List<ModSettingsEntry>();
			return Settings.FirstOrDefault(setting => setting.FilePath.Equals(path));
		}

		#endregion Settings
	}
}