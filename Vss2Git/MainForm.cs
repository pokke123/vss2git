﻿/* Copyright 2009 HPDI, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Elcom.Utils;
using Hpdi.VssLogicalLib;
using Mono.Options;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Main form for the application.
    /// </summary>
    /// <author>Trevor Robinson</author>
    public partial class MainForm : Form
    {
        private const string vcsTypeGit = "git";
        private const string vcsTypeSvn = "svn";
        private const string emailPropertiesFileName = "emails.properties";
        private const string emailUserNamesListMessage = "The list of usernames is written to:\n\n{0}\n\n" +
                        "Please edit it and fill in email addresses in the form:\n\n" +
                        "username = Full Name <e-mail>\n\nor\n\nusername = e-mail";
        private const string emailUserNamesListCaption = "User-email mapping";

        private readonly Dictionary<int, EncodingInfo> codePages = new Dictionary<int, EncodingInfo>();
        private readonly WorkQueue workQueue = new WorkQueue(1);
        private readonly WorkQueue backgroundQueue = new WorkQueue(1);

        private Logger logger = Logger.Null;
        private RevisionAnalyzer revisionAnalyzer;
        private ChangesetBuilder changesetBuilder;
        private string settingsFile;

        private DateTime? continueAfter;

        private OptionSet optionSet;
        private bool goAndExit;
        private bool exitOnComplete;

        public MainForm(string[] args)
        {
            InitializeComponent();
            parseCommandLine(args);
            AdvancedTaskbar.Init(this);
        }


        private void parseCommandLine(string[] args)
        {
            try
            {
                optionSet = new OptionSet() {
                    { "s|settings=",  "the settings {FILE}",
                       v => settingsFile = v },
                    { "go",  "perform conversion and exit",
                       v => goAndExit = v != null },
                    { "h|help",  "show this message and exit",
                       v => { if (v != null) showHelp(); } },
                };
                var extra = optionSet.Parse(args);
                if (extra.Count > 0)
                    throw new OptionException("Unknown option: " + extra[0], extra[0]);
            }
            catch (OptionException ex)
            {
                MessageBox.Show("Invalid command line: " + ex.Message + "\n\n"
                    + "Try `--help` for more information.", "VSS2Git",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void showHelp()
        {
            var sw = new StringWriter();
            optionSet.WriteOptionDescriptions(sw);
            MessageBox.Show("Usage: " + Path.GetFileName(System.Reflection.Assembly.GetEntryAssembly().Location)
                + " <options>\nWhere <options> are:\n" + sw,
                "VSS2Git Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        private void OpenLog(string filename)
        {
            logger?.Dispose();
            logger = string.IsNullOrEmpty(filename) ? Logger.Null : new Logger(filename);
            VssUtil.logger = logger;
        }

        private void LoadRepoSettings()
        {
            Invoke((MethodInvoker)delegate
            {
                Encoding enc = (transcodeCheckBox.Checked ? Encoding.UTF8 : Encoding.Default);
                IVcsWrapper vcsWrapper = outDirTextBox.Text.Length > 0 ? CreateVcsWrapper(enc) : null;
                backgroundQueue.AddLast(delegate
                {
                    continueAfter = vcsWrapper != null ? vcsWrapper.GetLastCommit() : null;
                    Invoke((MethodInvoker)delegate
                    {
                        if (continueAfter == null)
                        {
                            if (continueSyncCheckBox.Checked)
                                resetRepoCheckBox.Checked = true;
                            continueSyncCheckBox.Enabled = continueSyncCheckBox.Checked = false;
                        } else
                        {
                            resetRepoCheckBox.Checked = false;
                            continueSyncCheckBox.Enabled = true;
                            continueSyncCheckBox.Checked = true;
                            continueSyncCheckBox.Text = "Continue sync from " + continueAfter;
                        }

                    });
                });
            });
        }

        private void goButton_Click(object sender, EventArgs e)
        {
            try
            {
                AdvancedTaskbar.EnableItermediate();
                OpenLog(logTextBox.Text);

                logger.WriteLine("VSS2Git version {0}", Assembly.GetExecutingAssembly().GetName().Version);

                WriteSettings();

                Encoding encoding = Encoding.Default;
                EncodingInfo encodingInfo;
                if (codePages.TryGetValue(encodingComboBox.SelectedIndex, out encodingInfo))
                {
                    encoding = encodingInfo.GetEncoding();
                }

                logger.WriteLine("VSS encoding: {0} (CP: {1}, IANA: {2})",
                    encoding.EncodingName, encoding.CodePage, encoding.WebName);
                logger.WriteLine("Comment transcoding: {0}",
                    transcodeCheckBox.Checked ? "enabled" : "disabled");
                logger.WriteLine("Ignore VCS errors: {0}",
                    ignoreVcsErrorsCheckBox.Checked ? "enabled" : "disabled");
                logger.WriteLine("Collapsing path of output directory",
                    RemovePathTextBox.Text);

                var df = new VssDatabaseFactory(vssDirTextBox.Text);
                df.Encoding = encoding;
                var db = df.Open();


                //start here


                // read the emails dictionary
                var emailDictionary = ReadDictionaryFile("e-mail dictionary", db.BasePath, emailPropertiesFileName);

                revisionAnalyzer = new RevisionAnalyzer(workQueue, logger, db);
                if (!string.IsNullOrEmpty(excludeTextBox.Text))
                {
                    revisionAnalyzer.ExcludeFiles = excludeTextBox.Text;
                }

                var paths = vssProjectTextBox.Text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var path in paths)
                {
                    VssItem item;
                    try
                    {
                        item = db.GetItem(path.Trim());
                    }
                    catch (VssPathException ex)
                    {
                        MessageBox.Show(ex.Message, "Invalid project path",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var project = item as VssProject;
                    if (project == null)
                    {
                        MessageBox.Show(path + " is not a project", "Invalid project path",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    revisionAnalyzer.AddItem(project);
                }


                changesetBuilder = new ChangesetBuilder(workQueue, logger, revisionAnalyzer);
                changesetBuilder.AnyCommentThreshold = TimeSpan.FromSeconds((double)anyCommentUpDown.Value);
                changesetBuilder.SameCommentThreshold = TimeSpan.FromSeconds((double)sameCommentUpDown.Value);
                changesetBuilder.BuildChangesets();

                if (!string.IsNullOrEmpty(outDirTextBox.Text))
                {
                    IVcsWrapper vcsWrapper = CreateVcsWrapper(encoding);

                    var vcsExporter = new VcsExporter(workQueue, logger,
                        revisionAnalyzer, changesetBuilder, vcsWrapper, emailDictionary);
                    if (!string.IsNullOrEmpty(domainTextBox.Text))
                    {
                        vcsExporter.EmailDomain = domainTextBox.Text;
                    }
                    if (!string.IsNullOrEmpty(commentTextBox.Text))
                    {
                        vcsExporter.DefaultComment = commentTextBox.Text;
                    }
                    if (!transcodeCheckBox.Checked)
                    {
                        vcsExporter.CommitEncoding = encoding;
                    }
                    vcsExporter.RemovePath = RemovePathTextBox.Text;
                    vcsExporter.TryGenerateCommitMessage = tryGenerateCommitMessageCheckBox.Checked;
                    vcsExporter.IgnoreVcsErrors = ignoreVcsErrorsCheckBox.Checked;
                    vcsExporter.ResetRepo = resetRepoCheckBox.Checked;
                    vcsExporter.FolderBeforeLabel = FolderBeforeLabel.Checked;
                    if (vcsExporter.ResetRepo)
                    {
                        vcsExporter.ExportToVcs(outDirTextBox.Text, null);
                    } else
                    {
                        vcsExporter.ExportToVcs(outDirTextBox.Text, continueAfter);
                    }
                }

                workQueue.Idle += delegate
                {
                    logger.Dispose();
                    logger = Logger.Null;
                    LoadRepoSettings();
                };

                statusTimer.Enabled = true;
                emailMap.Enabled = false;
                goButton.Enabled = false;
                cancelButton.Text = "Cancel";
            }
            catch (Exception ex)
            {
                logger.Dispose();
                logger = Logger.Null;
                ShowException(ex);
            }
        }

        private IVcsWrapper CreateVcsWrapper(Encoding commitEncoding)
        {
            Invoke((MethodInvoker)delegate
            { if (transcodeCheckBox.Checked) commitEncoding = Encoding.UTF8; });
            string repoPath = outDirTextBox.Text;
            string vcsType = vcsSetttingsTabs.SelectedTab.Text;
            if (vcsType.Equals(vcsTypeGit))
            {
                GitWrapper wrapper = new GitWrapper(repoPath, logger, commitEncoding, forceAnnotatedCheckBox.Checked);
                wrapper.GitIgnoreInfo = $"{ignoreFile.Text}|{attributesFile.Text}|{userName.Text}|{userEmail.Text}|{initialComment.Text}";
                return wrapper;
            } else if (vcsType.Equals(vcsTypeSvn))
            {
                bool stdLayout = svnStandardLayoutCheckBox.Checked;
                string trunk = stdLayout ? SvnWrapper.stdTrunk : svnTrunkTextBox.Text;
                string tags = stdLayout ? SvnWrapper.stdTags : svnTagsTextBox.Text;
                string branches = stdLayout ? SvnWrapper.stdBranches : svnBranchesTextBox.Text;
                var wrapper = new SvnWrapper(repoPath, svnRepoTextBox.Text, svnProjectPathTextBox.Text,
                    trunk, tags, branches, logger);
                wrapper.SetCredentials(svnUserTextBox.Text, svnPasswordTextBox.Text);
                return wrapper;
            }
            throw new ArgumentOutOfRangeException("vcsType", vcsType, "Undefined VCS Type");
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            if (goButton.Enabled)
            {
                Close();
            } else
            {
                workQueue.Abort();
            }
        }

        private void statusTimer_Tick(object sender, EventArgs e)
        {
            statusLabel.Text = workQueue.LastStatus ?? "Idle";
            timeLabel.Text = string.Format("Elapsed: {0:HH:mm:ss}",
                new DateTime(workQueue.ActiveTime.Ticks));

            if (revisionAnalyzer != null)
            {
                fileLabel.Text = "Files: " + revisionAnalyzer.FileCount;
                revisionLabel.Text = "Revisions: " + revisionAnalyzer.RevisionCount;
            }

            if (changesetBuilder != null)
            {
                changeLabel.Text = "Changesets: " + changesetBuilder.Changesets.Count;
                if (changesetBuilder.Changesets.Count > 1)
                    AdvancedTaskbar.EnableProgress((uint)changesetBuilder.Changesets.Count);
            }

            if (workQueue.IsIdle)
            {
                revisionAnalyzer = null;
                changesetBuilder = null;

                statusTimer.Enabled = false;
                if (!goButton.Enabled)
                {
                    LoadRepoSettings();
                }
                emailMap.Enabled = true;
                goButton.Enabled = true;
                AdvancedTaskbar.Disable();
                cancelButton.Text = "Close";
                if (goAndExit && exitOnComplete)
                {
                    exitOnComplete = false;
                    Close();
                }
            }

            var exceptions = workQueue.FetchExceptions();
            if (exceptions != null)
            {
                foreach (var exception in exceptions)
                {
                    ShowException(exception);
                }
            }
        }

        private void ShowException(Exception exception)
        {
            var message = ExceptionFormatter.Format(exception);
            logger.WriteLine("ERROR: {0}", message);
            logger.WriteLine(exception);

            MessageBox.Show(message, "Unhandled Exception",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this.Text += " " + Assembly.GetExecutingAssembly().GetName().Version;

            var defaultCodePage = Encoding.Default.CodePage;
            var description = string.Format("System default - {0}", Encoding.Default.EncodingName);
            var defaultIndex = encodingComboBox.Items.Add(description);
            encodingComboBox.SelectedIndex = defaultIndex;

            var encodings = Encoding.GetEncodings();
            foreach (var encoding in encodings)
            {
                var codePage = encoding.CodePage;
                description = string.Format("CP{0} - {1}", codePage, encoding.DisplayName);
                var index = encodingComboBox.Items.Add(description);
                codePages[index] = encoding;
                if (codePage == defaultCodePage)
                {
                    codePages[defaultIndex] = encoding;
                }
            }

            ReadSettings();
            LoadRepoSettings();
            if (goAndExit)
            {
                goButton_Click(null, null);
                exitOnComplete = true;
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            WriteSettings();

            workQueue.Abort();
            workQueue.WaitIdle();
        }

        private void ReadSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(settingsFile))
                {
                    LoadSettings(settingsFile);
                    return;
                }
            }
            catch (Exception x)
            {
                ShowException(x);
            }
            var settings = Properties.Settings.Default;
            DisplaySettings(settings);
        }

        private void WriteSettings()
        {
            var settings = Properties.Settings.Default;
            UpdateSettings(settings);
            settings.Save();
        }

        private void SaveSettings()
        {
            try
            {
                var settings = Properties.Settings.Default;
                string lastSettingsFile = settings.LastSettingsFile;
                if (lastSettingsFile == "")
                    lastSettingsFile = "vss2git.properties";
                settingsSaveFileDialog.InitialDirectory = Path.GetDirectoryName(lastSettingsFile);
                settingsSaveFileDialog.FileName = Path.GetFileName(lastSettingsFile);
                if (DialogResult.OK == settingsSaveFileDialog.ShowDialog(this))
                {
                    string fileName = settingsSaveFileDialog.FileName;
                    settings.LastSettingsFile = fileName;
                    UpdateSettings(settings);
                    var values = settings.PropertyValues;
                    Dictionary<string, string> dictionary = new Dictionary<string, string>();
                    foreach (SettingsPropertyValue value in values)
                    {
                        if (!value.UsingDefaultValue && !"LastSettingsFile".Equals(value.Name))
                        {
                            dictionary.Add(value.Name, value.SerializedValue.ToString());
                        }
                    }
                    WriteDictionaryFile(dictionary, fileName);
                }
            }
            catch (Exception x)
            {
                ShowException(x);
            }
        }

        private void LoadSettings()
        {
            try
            {
                this.ignoreVcsErrorsCheckBox.Checked = false;
                var settings = Properties.Settings.Default;
                string lastSettingsFile = settings.LastSettingsFile;
                if (!string.IsNullOrEmpty(lastSettingsFile))
                {
                    settingsOpenFileDialog.InitialDirectory = Path.GetDirectoryName(lastSettingsFile);
                }
                settingsOpenFileDialog.FileName = Path.GetFileName(lastSettingsFile);
                if (DialogResult.OK == settingsOpenFileDialog.ShowDialog(this))
                {
                    LoadSettings(settingsOpenFileDialog.FileName);
                }
            }
            catch (Exception x)
            {
                ShowException(x);
            }
        }

        private void LoadSettings(string fileName)
        {
            IDictionary<string, string> dictionary = ReadDictionaryFile(fileName);
            var settings = Properties.Settings.Default;
            settings.LastSettingsFile = fileName;
            var values = settings.PropertyValues;
            foreach (SettingsPropertyValue value in values)
            {
                if (dictionary.ContainsKey(value.Name))
                {
                    // it seems to be impossible to write to values directly, so we use a newly created one
                    SettingsPropertyValue newValue = new System.Configuration.SettingsPropertyValue(value.Property);
                    newValue.SerializedValue = dictionary[value.Name];
                    settings[value.Name] = newValue.PropertyValue;
                }
            }
            DisplaySettings(settings);
        }

        private void DisplaySettings(Properties.Settings settings)
        {
            vssDirTextBox.Text = settings.VssDirectory;
            vssProjectTextBox.Text = settings.VssProject;
            excludeTextBox.Text = settings.VssExcludePaths;
            outDirTextBox.Text = settings.OutDirectory;
            domainTextBox.Text = settings.DefaultEmailDomain;
            commentTextBox.Text = settings.DefaultComment;
            logTextBox.Text = settings.LogFile;
            transcodeCheckBox.Checked = settings.TranscodeComments;
            resetRepoCheckBox.Checked = settings.ResetRepo || settings.ContinueSync;
            tryGenerateCommitMessageCheckBox.Checked = settings.TryGenerateCommitMessage;
            forceAnnotatedCheckBox.Checked = settings.ForceAnnotatedTags;
            anyCommentUpDown.Value = settings.AnyCommentSeconds;
            sameCommentUpDown.Value = settings.SameCommentSeconds;
            svnRepoTextBox.Text = settings.SvnRepo;
            svnProjectPathTextBox.Text = settings.SvnProjectPath;
            svnUserTextBox.Text = String.IsNullOrEmpty(settings.SvnUser) ?
                System.Environment.UserName : settings.SvnUser;
            svnPasswordTextBox.Text = settings.SvnPassword;
            svnStandardLayoutCheckBox.Checked = settings.SvnStandardLayout;
            svnTrunkTextBox.Text = settings.SvnTrunk;
            svnTagsTextBox.Text = settings.SvnTags;
            svnBranchesTextBox.Text = settings.SvnBranches;
            ignoreFile.Text = settings.GitFirstCommitIgnoreFile;
            attributesFile.Text = settings.GitFirstCommitAttributesFile;
            userName.Text = settings.GitFirstCommitUserName;
            userEmail.Text = settings.GitFirstCommitUserMail;
            initialComment.Text = settings.GitFirstCommitComment;
            RemovePathTextBox.Text = settings.RemovePath;
            FolderBeforeLabel.Checked = settings.FolderBeforeLabel;

            int index = 0;
            int count = vcsSetttingsTabs.TabPages.Count;
            for (int i = 0; i < count; i++)
            {
                if (vcsSetttingsTabs.TabPages[i].Text.Equals(settings.VcsType))
                {
                    index = i;
                    break;
                }
            }
            vcsSetttingsTabs.SelectTab(index);
        }

        private void UpdateSettings(Properties.Settings settings)
        {
            settings.VssDirectory = vssDirTextBox.Text;
            settings.VssProject = vssProjectTextBox.Text;
            settings.VssExcludePaths = excludeTextBox.Text;
            settings.OutDirectory = outDirTextBox.Text;
            settings.DefaultEmailDomain = domainTextBox.Text;
            settings.DefaultComment = commentTextBox.Text;
            settings.LogFile = logTextBox.Text;
            settings.TranscodeComments = transcodeCheckBox.Checked;
            settings.ResetRepo = resetRepoCheckBox.Checked;
            settings.TryGenerateCommitMessage = tryGenerateCommitMessageCheckBox.Checked;
            settings.ContinueSync = continueSyncCheckBox.Checked;
            settings.ForceAnnotatedTags = forceAnnotatedCheckBox.Checked;
            settings.AnyCommentSeconds = (int)anyCommentUpDown.Value;
            settings.SameCommentSeconds = (int)sameCommentUpDown.Value;
            settings.VcsType = vcsSetttingsTabs.SelectedTab.Text;
            settings.SvnRepo = svnRepoTextBox.Text;
            settings.SvnProjectPath = svnProjectPathTextBox.Text;
            settings.SvnUser = System.Environment.UserName.Equals(svnUserTextBox.Text) ?
                "" : svnUserTextBox.Text;
            settings.SvnPassword = svnPasswordTextBox.Text;
            settings.SvnStandardLayout = svnStandardLayoutCheckBox.Checked;
            settings.SvnTrunk = svnTrunkTextBox.Text;
            settings.SvnTags = svnTagsTextBox.Text;
            settings.SvnBranches = svnBranchesTextBox.Text;
            settings.GitFirstCommitIgnoreFile = ignoreFile.Text;
            settings.GitFirstCommitAttributesFile = attributesFile.Text;
            settings.GitFirstCommitUserName = userName.Text;
            settings.GitFirstCommitUserMail = userEmail.Text;
            settings.GitFirstCommitComment = initialComment.Text;
            settings.RemovePath = RemovePathTextBox.Text;
            settings.FolderBeforeLabel = FolderBeforeLabel.Checked;
        }

        private IDictionary<string, string> ReadDictionaryFile(string fileKind, string repoPath, string fileName)
        {
            string finalPath = Path.Combine(repoPath, fileName);
            // read the properties file either from the repository path or from the working directory
            if (!File.Exists(finalPath))
            {
                finalPath = fileName;
            }
            if (File.Exists(finalPath))
            {
                try
                {
                    IDictionary<string, string> dictionary = ReadDictionaryFile(finalPath);
                    logger.WriteLine(dictionary.Count + " entries read from " + fileKind + " at " + finalPath);
                    return dictionary;
                }
                catch (Exception x)
                {
                    logger.WriteLine("error reading " + fileKind + " from " + finalPath + ": " + x.Message);
                }
            } else
            {
                // if the properties doesn't exist, return an empty dictionary
                logger.WriteLine(fileKind + " not found: " + finalPath);
            }
            return new Dictionary<string, string>();
        }

        private void WriteDictionaryFile(IDictionary<string, string> dictionary, string filePath)
        {
            string contents = "";
            foreach (KeyValuePair<string, string> entry in dictionary)
            {
                contents += entry.Key + "=" + entry.Value + "\r\n";
            }
            File.WriteAllText(filePath, contents);
        }

        private IDictionary<string, string> ReadDictionaryFile(string filePath)
        {
            Dictionary<string, string> dictionary = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(filePath))
            {
                // read lines that contain a '=' sign and skip comment lines starting with a '#'
                if ((!string.IsNullOrEmpty(line)) &&
                    (!line.StartsWith("#")) &&
                    (line.Contains("=")))
                {
                    int index = line.IndexOf('=');
                    string key = line.Substring(0, index).Trim();
                    string value = line.Substring(index + 1).Trim();
                    dictionary.Add(key, value);
                }
            }

            return dictionary;
        }

        private void DumpUsers()
        {
            var df = new VssDatabaseFactory(vssDirTextBox.Text);
            var db = df.Open();
            var path = vssProjectTextBox.Text;
            VssProject project;
            try
            {
                project = db.GetItem(path) as VssProject;
            }
            catch (VssPathException ex)
            {
                MessageBox.Show(ex.Message, "Invalid project path",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (project == null)
            {
                MessageBox.Show(path + " is not a project", "Invalid project path",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var emailDictionary = ReadDictionaryFile("e-mail dictionary", db.BasePath, emailPropertiesFileName);

            AdvancedTaskbar.EnableItermediate();
            this.statusTimer.Enabled = true;
            this.emailMap.Enabled = false;
            this.goButton.Enabled = false;
            this.cancelButton.Text = "Cancel";
            revisionAnalyzer = new RevisionAnalyzer(workQueue, logger, db);
            if (!string.IsNullOrEmpty(excludeTextBox.Text))
            {
                revisionAnalyzer.ExcludeFiles = excludeTextBox.Text;
            }
            revisionAnalyzer.AddItem(project);
            workQueue.AddLast(delegate (object work)
            {
                foreach (var dateEntry in revisionAnalyzer.SortedRevisions)
                {
                    foreach (Revision revision in dateEntry.Value)
                    {
                        var user = revision.User.ToLower();
                        if (emailDictionary.ContainsKey(user))
                            continue;
                        emailDictionary.Add(user, "");
                    }
                }
                string propsPath = Path.Combine(db.BasePath, emailPropertiesFileName);
                WriteDictionaryFile(emailDictionary, propsPath);
                this.BeginInvoke((MethodInvoker)delegate
                {
                    MessageBox.Show(this, string.Format(emailUserNamesListMessage, propsPath),
                        emailUserNamesListCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
        }

        private void svnStandardLayoutCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            bool enabled = !svnStandardLayoutCheckBox.Checked;
            svnTrunkTextBox.Enabled = enabled;
            svnTagsTextBox.Enabled = enabled;
            svnBranchesTextBox.Enabled = enabled;
        }

        private void vssDirButton_Click(object sender, EventArgs e)
        {
            SelectDirectory(vssDirBrowserDialog, vssDirTextBox);
        }

        private void outDirButton_Click(object sender, EventArgs e)
        {
            SelectDirectory(outDirBrowserDialog, outDirTextBox);
        }

        private void svnRepoButton_Click(object sender, EventArgs e)
        {
            SelectDirectory(svnRepoBrowserDialog, svnRepoTextBox);
        }

        private void SelectDirectory(FolderBrowserDialog folderBrowser, TextBox textBox)
        {
            string directory = textBox.Text;
            if (Directory.Exists(directory))
            {
                folderBrowser.SelectedPath = new DirectoryInfo(directory).FullName;
            }
            if (DialogResult.OK == folderBrowser.ShowDialog(this))
            {
                textBox.Text = folderBrowser.SelectedPath;
            }
        }

        private void saveSettingsButton_Click(object sender, EventArgs e)
        {
            SaveSettings();
        }

        private void loadSettingsButton_Click(object sender, EventArgs e)
        {
            LoadSettings();
        }

        private void emailMap_Click(object sender, EventArgs e)
        {
            DumpUsers();
        }

        private void resetRepoCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (resetRepoCheckBox.Checked)
            {
                continueSyncCheckBox.Checked = false;
            }
        }

        private void continueSyncCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (continueSyncCheckBox.Checked)
            {
                resetRepoCheckBox.Checked = false;
            }
        }

        private void outDirTextBox_TextChanged(object sender, EventArgs e)
        {
            LoadRepoSettings();
        }

        private void vcsSetttingsTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            LoadRepoSettings();
        }

        private void GitignoreFileButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                try
                {
                    dlg.FileName = this.ignoreFile.Text;
                    dlg.CheckPathExists = true;
                    dlg.CheckFileExists = true;
                    dlg.Multiselect = false;
                    dlg.Filter = "git ignore file|.gitignore";
                    dlg.Title = "Select initial .gitignore file";
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        this.ignoreFile.Text = dlg.FileName;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "VSS2Git", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void GitattributeFileButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                try
                {
                    dlg.FileName = this.ignoreFile.Text;
                    dlg.CheckPathExists = true;
                    dlg.CheckFileExists = true;
                    dlg.Multiselect = false;
                    dlg.Filter = "git attributes file|.gitattributes";
                    dlg.Title = "Select initial .gitattributes file";
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        this.ignoreFile.Text = dlg.FileName;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "VSS2Git", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SourceProjectChanged(object sender, EventArgs e)
        {
            this.ignoreVcsErrorsCheckBox.Checked = false;
        }
    }
}
