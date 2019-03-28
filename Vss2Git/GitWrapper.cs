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
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Wraps execution of Git and implements the common Git commands.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class GitWrapper : AbstractVcsWrapper
    {
        private const string gitMetaDir = ".git";
        private const string gitExecutable = "git";
        private const string gitIgnoreFile = ".gitignore";
        private const string gitAttributesFile = ".gitattributes";

        private List<string> addQueue = new List<string>();
        private List<string> deleteQueue = new List<string>();
        private List<string> dirDeleteQueue = new List<string>();

        private Encoding commitEncoding = Encoding.UTF8;
        private string gitIgnoreInfo;

        public Encoding CommitEncoding
        {
            get { return commitEncoding; }
            set { commitEncoding = value; }
        }

        public string GitIgnoreInfo
        {
            get { return gitIgnoreInfo; }
            set { gitIgnoreInfo = value; }
        }

        private bool forceAnnotatedTags = true;
        public bool ForceAnnotatedTags
        {
            get { return forceAnnotatedTags; }
            set { forceAnnotatedTags = value; }
        }

        public GitWrapper(string outputDirectory, Logger logger, Encoding commitEncoding,
            bool forceAnnotatedTags)
            : base(outputDirectory, logger, gitExecutable, gitMetaDir)
        {
            this.commitEncoding = commitEncoding;
            this.forceAnnotatedTags = forceAnnotatedTags;
        }

        public override string QuoteRelativePath(string path)
        {
            return base.QuoteRelativePath(path).Replace('\\', '/'); // cygwin git compatibility
        }

        public override void Init(bool resetRepo)
        {
            if (resetRepo)
            {
                DeleteDirectory(GetOutputDirectory());
                Thread.Sleep(0);
                Directory.CreateDirectory(GetOutputDirectory());
                VcsExec("init");
            }
        }

        public override void Init(Changeset changeset, string repoPath)
        {
            if ((!object.ReferenceEquals(changeset, null)) && (!string.IsNullOrEmpty(gitIgnoreInfo)))
            {
                string[] data = gitIgnoreInfo.Trim().Trim('|').Split('|');
                if (data.Length == 5)
                {
                    bool addFirstCommit = false;
                    if (!string.IsNullOrWhiteSpace(data[0]))
                    {
                        string myIgnoreFile = Path.Combine(data[0], gitIgnoreFile);
                        if (!File.Exists(myIgnoreFile))
                        {
                            myIgnoreFile = data[0];
                        }
                        if (File.Exists(myIgnoreFile))
                        {
                            File.Copy(myIgnoreFile, Path.Combine(repoPath, gitIgnoreFile), true);
                            addFirstCommit = true;
                            //DoAdd(gitIgnoreFile);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(data[1]))
                    {
                        string myAttrFile = Path.Combine(data[1], gitAttributesFile);
                        if (!File.Exists(myAttrFile))
                        {
                            myAttrFile = data[1];
                        }
                        if (File.Exists(myAttrFile))
                        {
                            File.Copy(myAttrFile, Path.Combine(repoPath, gitAttributesFile), true);
                            addFirstCommit = true;
                            //DoAdd(gitAttributesFile);
                        }
                    }
                    if (addFirstCommit)
                    {
                        AddAll();
                        Commit(data[2], data[3], data[4], changeset.DateTime.AddHours(-2));
                    }
                }
            }
        }

        public override void Configure(bool newRepo)
        {
            if (commitEncoding.WebName != "utf-8")
            {
                SetConfig("i18n.commitencoding", commitEncoding.WebName);
            }
            CheckOutputDirectory(newRepo);
        }

        public override bool Add(string path)
        {
            addQueue.Add(path);
            return true;
        }

        private bool DoAdd(string paths)
        {
            var startInfo = GetStartInfo("add -f --" + paths);

            // add fails if there are no files (directories don't count)
            bool result = ExecuteUnless(startInfo, "did not match any files");
            if (result)
                SetNeedsCommit();
            return result;
        }

        private bool DoAdds()
        {
            bool rc = false;
            string paths = "";
            foreach (string path in addQueue)
            {
                if (paths.Length > 8000)
                {
                    rc |= DoAdd(paths);
                    paths = "";
                }
                paths += " " + QuoteRelativePath(path);
            }
            addQueue.Clear();
            if (paths.Length > 1)
                rc |= DoAdd(paths);
            return rc;
        }

        public override bool AddDir(string path)
        {
            // do nothing - git does not care about directories
            return true;
        }
        public override bool NeedsCommit()
        {
            DoAdds();
            DoDeletes();
            return base.NeedsCommit();
        }

        public override bool AddAll()
        {
            var startInfo = GetStartInfo("add -A");

            // add fails if there are no files (directories don't count)
            bool result = ExecuteUnless(startInfo, "did not match any files");
            if (result)
                SetNeedsCommit();
            return result;
        }

        public override void RemoveFile(string path)
        {
            deleteQueue.Add(path);
            SetNeedsCommit();
        }

        private void DoDelete(string paths)
        {
            VcsExec("rm -r -f --" + paths); // is always recursive
        }

        private void DoDeletes()
        {
            string paths = "";
            foreach (string path in deleteQueue)
            {
                if (paths.Length > 8000)
                {
                    DoDelete(paths);
                    paths = "";
                }
                paths += " " + QuoteRelativePath(path);
            }
            deleteQueue.Clear();
            if (paths.Length > 1)
                DoDelete(paths);
            CleanupEmptyDirs();
        }
        private void CleanupEmptyDirs()
        {
            foreach (string dir in dirDeleteQueue)
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            dirDeleteQueue.Clear();
        }

        public override void RemoveDir(string path, bool recursive)
        {
            deleteQueue.Add(path); // is always recursive
            dirDeleteQueue.Add(path);
            SetNeedsCommit();
        }

        public override void RemoveEmptyDir(string path)
        {
            // do nothing - remove only on file system - git doesn't care about directories with no files
        }

        public override void Move(string sourcePath, string destPath)
        {
            if (sourcePath != destPath && !VcsExecUnless("mv -f -- " + QuoteRelativePath(sourcePath) + " " + QuoteRelativePath(destPath), "source directory is empty"))
            {
                Directory.Move(sourcePath, destPath);
            }
            SetNeedsCommit();
        }

        public override void MoveEmptyDir(string sourcePath, string destPath)
        {
            // move only on file system - git doesn't care about directories with no files
            Directory.Move(sourcePath, destPath);
        }

        public override bool DoCommit(string authorName, string authorEmail, string comment, DateTime localTime)
        {
            TempFile commentFile;

            var args = "commit";
            AddComment(comment, ref args, out commentFile);

            using (commentFile)
            {
                var startInfo = GetStartInfo(args);
                startInfo.EnvironmentVariables["GIT_AUTHOR_NAME"] = authorName;
                startInfo.EnvironmentVariables["GIT_AUTHOR_EMAIL"] = authorEmail;
                startInfo.EnvironmentVariables["GIT_AUTHOR_DATE"] = GetUtcTimeString(localTime);

                // also setting the committer is supposedly useful for converting to Mercurial
                startInfo.EnvironmentVariables["GIT_COMMITTER_NAME"] = authorName;
                startInfo.EnvironmentVariables["GIT_COMMITTER_EMAIL"] = authorEmail;
                startInfo.EnvironmentVariables["GIT_COMMITTER_DATE"] = GetUtcTimeString(localTime);

                // ignore empty commits, since they are non-trivial to detect
                // (e.g. when renaming a directory)
                return ExecuteUnless(startInfo, "nothing to commit");
            }
        }

        public override void Tag(string name, string taggerName, string taggerEmail, string comment, DateTime localTime)
        {
            TempFile commentFile;

            var args = "tag -f"; //TODO: check how to detect label deletions (doesn't seem to happen right now)
            // tools like Mercurial's git converter only import annotated tags
            // remark: annotated tags are created with the git -a option,
            // see e.g. http://learn.github.com/p/tagging.html
            if (forceAnnotatedTags)
            {
                args += " -a";
            }
            AddComment(comment, ref args, out commentFile);

            // tag names are not quoted because they cannot contain whitespace or quotes
            args += " -- " + name;

            using (commentFile)
            {
                var startInfo = GetStartInfo(args);
                startInfo.EnvironmentVariables["GIT_COMMITTER_NAME"] = taggerName;
                startInfo.EnvironmentVariables["GIT_COMMITTER_EMAIL"] = taggerEmail;
                startInfo.EnvironmentVariables["GIT_COMMITTER_DATE"] = GetUtcTimeString(localTime);

                ExecuteUnless(startInfo, null);
            }
        }

        private void SetConfig(string name, string value)
        {
            VcsExec("config " + name + " " + Quote(value));
        }

        private void AddComment(string comment, ref string args, out TempFile tempFile)
        {
            tempFile = null;
            if (!string.IsNullOrEmpty(comment))
            {
                // need to use a temporary file to specify the comment when not
                // using the system default code page or it contains newlines
                if (commitEncoding.CodePage != Encoding.Default.CodePage || comment.IndexOf('\n') >= 0)
                {
                    Logger.WriteLine("Generating temp file for comment: {0}", comment);
                    tempFile = new TempFile();
                    tempFile.Write(comment, commitEncoding);

                    // temporary path might contain spaces (e.g. "Documents and Settings")
                    args += " -F " + Quote(tempFile.Name);
                } else
                {
                    args += " -m " + Quote(comment);
                }
            } else
            {
                args += " --allow-empty-message --no-edit -m \"\"";
            }
        }

        private static string GetUtcTimeString(DateTime localTime)
        {
            // convert local time to UTC based on whether DST was in effect at the time
            var utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime);

            // format time according to ISO 8601 (avoiding locale-dependent month/day names)
            return utcTime.ToString("yyyy'-'MM'-'dd HH':'mm':'ss +0000");
        }

        private static Regex lastCommitTimestampRegex = new Regex("^Date:\\s*(\\S+)", RegexOptions.Multiline);

        public override DateTime? GetLastCommit()
        {
            if (Directory.Exists(Path.Combine(GetOutputDirectory(), gitMetaDir)) && FindExecutable())
            {
                try
                {
                    var startInfo = GetStartInfo("log -n 1 --date=raw");
                    string stdout, stderr;
                    int exitCode = Execute(startInfo, out stdout, out stderr);
                    if (exitCode == 0)
                    {
                        var m = lastCommitTimestampRegex.Match(stdout);
                        if (m.Success)
                        {
                            long unixTimeStamp = long.Parse(m.Groups[1].Value);
                            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
                            dt = dt.AddSeconds(unixTimeStamp).ToLocalTime();
                            return dt;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

    }
}
