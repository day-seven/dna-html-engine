﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Dna.HtmlEngine.Core
{
    /// <summary>
    /// A base engine that any specific engine should implement
    /// </summary>
    public abstract class BaseEngine : IDisposable
    {
        #region Private Members

        /// <summary>
        /// A list of folder watchers that listen out for file changes of the given extensions
        /// </summary>
        private List<FolderWatcher> mWatchers;

        /// <summary>
        /// The regex to match special tags containing up to 2 values
        /// For example: <!--@ include header --> to include the file header._dnaweb or header.dnaweb if found
        /// </summary>
        protected string mStandard2GroupRegex = @"<!--@\s*(\w+)\s*(.*?)\s*-->";

        #endregion

        #region Public Properties

        /// <summary>
        /// The paths to monitor for files
        /// </summary>
        public string MonitorPath { get; set; }

        /// <summary>
        /// The desired default output extension for generated files if not overridden
        /// </summary>
        public string OutputExtension { get; set; } = ".dna";

        /// <summary>
        /// The time in milliseconds to wait for file edits to stop occurring before processing the file
        /// </summary>
        public int ProcessDelay { get; set; } = 100;

        /// <summary>
        /// The filename extensions to monitor for
        /// All files: .*
        /// Specific types: .dnaweb
        /// </summary>
        public List<string> EngineExtensions { get; set; }

        #endregion

        #region Public Events

        /// <summary>
        /// Called when processing of a file succeeded
        /// </summary>
        public event Action<EngineProcessResult> ProcessSuccessful = (result) => { };

        /// <summary>
        /// Called when processing of a file failed
        /// </summary>
        public event Action<EngineProcessResult> ProcessFailed = (result) => { };

        /// <summary>
        /// Called when the engine started
        /// </summary>
        public event Action Started = () => { };

        /// <summary>
        /// Called when the engine stopped
        /// </summary>
        public event Action Stopped = () => { };

        /// <summary>
        /// Called when the engine started watching for a specific file extension
        /// </summary>
        public event Action<string> StartedWatching = (extension) => { };

        /// <summary>
        /// Called when the engine stopped watching for a specific file extension
        /// </summary>
        public event Action<string> StoppedWatching = (extension) => { };

        /// <summary>
        /// Called when a log message is raised
        /// </summary>
        public event Action<LogMessage> LogMessage = (message) => { };

        #endregion

        #region Abstract Methods

        /// <summary>
        /// The processing action to perform when the given file has been edited
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        protected abstract Task<EngineProcessResult> ProcessFile(string path);

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public BaseEngine()
        {

        }

        #endregion

        #region Engine Methods

        /// <summary>
        /// Starts the engine ready to handle processing of the specified files
        /// </summary>
        public void Start()
        {
            // Lock this class so only one call can happen at a time
            lock (this)
            {
                // Dipose of any previous engine setup
                Dispose();

                // Make sure we have extensions
                if (this.EngineExtensions?.Count == 0)
                    throw new InvalidOperationException("No engine extensions specified");

                // Load settings
                LoadSettings();

                // Resolve path
                if (!Path.IsPathRooted(MonitorPath))
                    MonitorPath = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, MonitorPath));

                // Let listener know we started
                Started();

                // Log the message
                Log($"Engine started listening to '{this.MonitorPath}' with {this.ProcessDelay}ms delay...");
                    
                // Create a new list of watchers
                mWatchers = new List<FolderWatcher>();

                // We need to listen out for file changes per extension
                EngineExtensions.ForEach(extension => mWatchers.Add(new FolderWatcher
                {
                    Filter = "*" + extension, 
                    Path = MonitorPath,
                    UpdateDelay = ProcessDelay
                }));

                // Listen on all watchers
                mWatchers.ForEach(watcher =>
                {
                    // Listen for file changes
                    watcher.FileChanged += Watcher_FileChanged;

                    // Inform listener
                    StartedWatching(watcher.Filter);

                    // Log the message
                    Log($"Engine listening for file type {watcher.Filter}");

                    // Start watcher
                    watcher.Start();
                });
            }
        }

        /// <summary>
        /// Loads settings from a dna.config file
        /// </summary>
        private void LoadSettings()
        {
            // Default monitor path of this folder
            MonitorPath = System.AppContext.BaseDirectory;

            // Read config file for monitor path
            try
            {
                var configFile = Path.Combine(System.AppContext.BaseDirectory, "dna.config");
                if (File.Exists(configFile))
                {
                    var configData = File.ReadAllLines(configFile);

                    // Try and find line starting with monitor: 
                    var monitor = configData.FirstOrDefault(f => f.StartsWith("monitor: "));

                    // If we didn't find it, ignore
                    if (monitor == null)
                        return;

                    // Otherwise, load the monitor path
                    monitor = monitor.Substring("monitor: ".Length);

                    // Convert path to full path
                    if (Path.IsPathRooted(monitor))
                        MonitorPath = monitor;
                    // Else resolve the relative path
                    else
                        MonitorPath = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, monitor));

                    // Log it
                    Log($"Monitor path set to: {MonitorPath}");
                }
            }
            // Don't care about config file failures other than logging it
            catch (Exception ex)
            {
                Log("Failed to read or process dna.config file", message: ex.Message, type: LogType.Warning);
            }
        }

        #endregion

        #region File Changed

        /// <summary>
        /// Fired when a watcher has detected a file change
        /// </summary>
        /// <param name="path">The path of the file that has changed</param>
        private async void Watcher_FileChanged(string path)
        {
            // Process the file
            await ProcessFileChanged(path);
        }

        /// <summary>
        /// Called when a file has changed and needs processing
        /// </summary>
        /// <param name="path">The full path of the file to process</param>
        /// <returns></returns>
        protected async Task ProcessFileChanged(string path)
        {
            try
            {
                // Log the start
                Log($"Processing file {path}...", type: LogType.Information);

                // Process the file
                var result = await ProcessFile(path);

                // Check if we have an unknown response
                if (result == null)
                    throw new ArgumentNullException("Unknown error processing file. No result provided");

                // If we succeeded, let the listeners know
                if (result.Success)
                {
                    // Inform listeners
                    ProcessSuccessful(result);

                    // Log the message
                    Log($"Successfully processed file {path}", type: LogType.Success);
                }
                // If we failed, let the listeners know
                else
                {
                    // Inform listeners
                    ProcessFailed(result);

                    // Log the message
                    Log($"Failed to processed file {path}", message: result.Error, type: LogType.Error);
                }
            }
            // Catch any unexpected failures
            catch (Exception ex)
            {
                // Generate an unexpected error report
                ProcessFailed(new EngineProcessResult
                {
                    Path = path,
                    Error = ex.Message,
                    Success = false,
                });

                // Log the message
                Log($"Unexpected fail to processed file {path}", message: ex.Message, type: LogType.Error);
            }
        }

        #endregion

        #region Commands

        /// <summary>
        /// Processes the tags in the list and edits the files contents as required
        /// </summary>
        /// <param name="path">The file that is being edit</param>
        /// <param name="fileContents">The full contents of the file</param>
        /// <param name="outputPaths">The output paths, can be changed by tags</param>
        /// <param name="isPartial">Set to true if the file is a partial and should not generate output itself</param>
        /// <param name="match">The tags to process</param>
        /// <param name="error">Set the error if there is a failure</param>
        public bool ProcessBaseTags(string path, ref string fileContents, ref bool isPartial, List<string> outputPaths, out string error)
        {
            // Find all special tags that have 2 groups
            var match = Regex.Match(fileContents, mStandard2GroupRegex, RegexOptions.Singleline);

            // No error to start with
            error = string.Empty;

            //
            // NOTE: Only look for the partial tag on the first run as it must be the top of the file
            //       and after that includes could end up adding partials to the parent confusing the situation
            //
            //       So make sure partials are set at the top of the file
            //
            var firstMatch = true;

            // Keep track of all includes to monitor for circular references
            var includes = new List<string>();

            // Loop through all matches
            while (match.Success)
            {
                // NOTE: The first group is the full match
                //       The second group and onwards are the matches

                // Make sure we have enough groups
                if (match.Groups.Count < 2)
                {
                    error = $"Malformed match {match.Value}";
                    return false;
                }

                // Take the first match as the header for the type of tag
                var tagType = match.Groups[1].Value.ToLower().Trim();

                // Now process each tag type
                switch (tagType)
                {
                    // PARTIAL CLASS
                    case "partial":

                        // Only update flag if it is the first match
                        // so includes don't mess it up
                        if (firstMatch)
                            isPartial = true;

                        // Remove tag
                        ReplaceTag(ref fileContents, match, string.Empty);

                        break;

                    // OUTPUT NAME
                    case "output":

                        // Make sure we have enough groups
                        if (match.Groups.Count < 3)
                        {
                            error = $"Malformed match {match.Value}";
                            return false;
                        }

                        // Get output path
                        var outputPath = match.Groups[2].Value;

                        // Process the output command
                        if (!ProcessCommandOutput(path, ref fileContents, outputPaths, outputPath, match, out error))
                            // Return false if it fails
                            return false;

                        break;

                    // INCLUDE (Replace file)
                    case "include":

                        // Make sure we have enough groups
                        if (match.Groups.Count < 3)
                        {
                            error = $"Malformed match {match.Value}";
                            return false;
                        }

                        // Get include path
                        var includePath = match.Groups[2].Value;

                        // Make sure we have not already included it in this run
                        if (includes.Contains(includePath.ToLower().Trim()))
                        {
                            error = $"Circular reference detected {includePath}";
                            return false;
                        }

                        // Process the include command
                        if (!ProcessCommandInclude(path, ref fileContents, outputPaths, includePath, match, out error))
                            // Return false if it fails
                            return false;

                        // Add this to the list of already processed includes
                        includes.Add(includePath.ToLower().Trim());

                        break;

                    // UNKNOWN
                    default:
                        // Report error of unknown match
                        error = $"Unknown match {match.Value}";
                        return false;
                }

                // Find the next command
                match = Regex.Match(fileContents, mStandard2GroupRegex, RegexOptions.Singleline);

                // No longer the first match
                firstMatch = false;
            }

            return true;
        }

        /// <summary>
        /// Processes an Output name command to add an output path
        /// </summary>
        /// <param name="path">The file that is being edit</param>
        /// <param name="fileContents">The full file contents to edit</param>
        /// <param name="outputPaths">The list of output names, can be changed by tags</param>
        /// <param name="outputPath">The include path, typically a relative path</param>
        /// <param name="match">The original match that found this information</param>
        /// <param name="error">Set the error if there is a failure</param>
        /// <returns></returns>
        protected bool ProcessCommandOutput(string path, ref string fileContents, List<string> outputPaths, string outputPath, Match match, out string error)
        {
            // No error to start with
            error = string.Empty;

            // Get the full path from the provided relative path based on the input files location
            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), outputPath));

            // Add this to the list
            outputPaths.Add(fullPath);

            // Remove the tag
            ReplaceTag(ref fileContents, match, string.Empty);

            return true;
        }

        /// <summary>
        /// Processes an Include command to replace a tag with the contents of another file
        /// </summary>
        /// <param name="path">The file that is being edit</param>
        /// <param name="fileContents">The full file contents to edit</param>
        /// <param name="includePath">The include path, typically a relative path</param>
        /// <param name="outputPaths">The list of output names, can be changed by tags</param>
        /// <param name="match">The original match that found this information</param>
        /// <param name="error">Set the error if there is a failure</param>
        protected bool ProcessCommandInclude(string path, ref string fileContents, List<string> outputPaths, string includePath, Match match, out string error)
        {
            // No error to start with
            error = string.Empty;

            // Try and find the include file
            var includedContents = FindIncludeFile(path, includePath, out string resolvedPath);

            // If we didn't find it, error out
            if (includedContents == null)
            {
                error = $"Include file not found {includePath}";
                return false;
            }

            // Otherwise we got it, so replace the tag with the contents
            ReplaceTag(ref fileContents, match, includedContents);

            // All done
            return true;
        }

        #region Private Helpers

        /// <summary>
        /// Searches for an input file in certain locations relative to the input file
        /// and returns the contents of it if found. 
        /// Returns null if not found
        /// </summary>
        /// <param name="path">The input path of the orignal file</param>
        /// <param name="includePath">The include path of the file trying to be included</param>
        /// <param name="resolvedPath">The resolved full path of the file that was found to be included</param>
        /// <param name="returnContents">True to return the files actual contents, false to return an empty string if found and null otherwise</param>
        /// <returns></returns>
        protected string FindIncludeFile(string path, string includePath, out string resolvedPath, bool returnContents = true)
        {
            // No path yet
            resolvedPath = null;

            // First look in the same folder
            var foundPath = Path.Combine(Path.GetDirectoryName(path), includePath);

            // If we found it, return contents
            if (FileManager.FileExists(foundPath))
            {
                // Set the resolved path
                resolvedPath = foundPath;

                // Return the contents
                return returnContents ? File.ReadAllText(foundPath) : string.Empty;
            }

            // Not found
            return null;
        }

        /// <summary>
        /// Replaces a given Regex match with the contents
        /// </summary>
        /// <param name="originalContent">The original contents to edit</param>
        /// <param name="match">The regex match to replace</param>
        /// <param name="newContent">The content to replace the match with</param>
        protected void ReplaceTag(ref string originalContent, Match match, string newContent)
        {
            // If the match is at the start, replace it
            if (match.Index == 0)
                originalContent = newContent + originalContent.Substring(match.Length);
            // Otherwise do an inner replace
            else
                originalContent = string.Concat(originalContent.Substring(0, match.Index), newContent, originalContent.Substring(match.Index + match.Length));
        }

        #endregion

        #endregion

        #region Find References

        /// <summary>
        /// Searches the <see cref="MonitorPath"/> for all files that match the <see cref="EngineExtensions"/> 
        /// then searches inside them to see if they include the includePath passed in />
        /// </summary>
        /// <param name="includePath">The path to look for being included in any of the files</param>
        /// <returns></returns>
        protected List<string> FindReferencedFiles(string includePath)
        {
            // New empty list
            var toProcess = new List<string>();

            // If we have no path, return 
            if (string.IsNullOrWhiteSpace(includePath))
                return toProcess;

            // Find all files in the monitor path
            var allFiles = Directory.EnumerateFiles(this.MonitorPath, "*.*", SearchOption.AllDirectories)
                .Where(file => this.EngineExtensions.Any(ex => Regex.IsMatch(Path.GetFileName(file), ex)))
                .ToList();
            
            // For each file, find all resolved references
            allFiles.ForEach(file =>
            {
                // Get all resolved references
                var references = GetResolvedIncludePaths(file);

                // If any match this file...
                if (references.Any(reference => string.Equals(reference, includePath, StringComparison.CurrentCultureIgnoreCase)))
                    // Add this file to be processed
                    toProcess.Add(file);
            });

            // Return what we found
            return toProcess;
        }

        /// <summary>
        /// Returns a list of resolved paths for all include files in a file
        /// </summary>
        /// <param name="filePath">The full path to the file to check</param>
        /// <returns></returns>
        protected List<string> GetResolvedIncludePaths(string filePath)
        {
            // New blank list
            var paths = new List<string>();

            // Make sure the file exists
            if (!FileManager.FileExists(filePath))
                return paths;

            // Read all the file into memory (it's ok we will never have large files they are text web files)
            var fileContents = FileManager.ReadAllText(filePath);

            // Create a match variable
            Match match = null;

            // Go though all matches
            while (match == null || match.Success)
            {
                // If we have already run a match once...
                if (match != null)
                    // Remove previous tag and carry on
                    ReplaceTag(ref fileContents, match, string.Empty);

                // Find all special tags that have 2 groups
                match = Regex.Match(fileContents, mStandard2GroupRegex, RegexOptions.Singleline);

                // Make sure we have enough groups
                if (match.Groups.Count < 3)
                    continue;

                // Get include path
                var includePath = match.Groups[2].Value;

                // Try and find the include file
                FindIncludeFile(filePath, includePath, out string resolvedPath, returnContents: false);

                // Add the resolved path if we got one
                if (!string.IsNullOrEmpty(resolvedPath))
                    paths.Add(resolvedPath);
            }

            // Return the results
            return paths;
        }

        #endregion

        #region Output

        /// <summary>
        /// Changes the file extension to the default output file extension
        /// </summary>
        /// <param name="path">The full path to the file</param>
        /// <returns></returns>
        protected string GetDefaultOutputPath(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + this.OutputExtension);
        }

        #endregion

        #region Logger

        /// <summary>
        /// Logs a message and raises the <see cref="LogMessage"/> event
        /// </summary>
        /// <param name="title">The title of the log</param>
        /// <param name="message">The main message of the log</param>
        /// <param name="type">The type of the log message</param>
        public void Log(string title, string message = "", LogType type = LogType.Diagnostic)
        {
            LogMessage(new LogMessage
            {
                Title = title,
                Message = message,
                Time = DateTime.UtcNow,
                Type = type
            });
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            // Clean up all file watchers
            mWatchers?.ForEach(watcher =>
            {
                // Get extension
                var extension = watcher.Filter;

                // Dispose of watcher
                watcher.Dispose();

                // Inform listener
                StoppedWatching(extension);

                // Log the message
                Log($"Engine stopped listening for file type {watcher.Filter}");
            });

            if (mWatchers != null)
            {
                // Let listener know we stopped
                Stopped();

                // Log the message
                Log($"Engine stopped");
            }

            mWatchers = null;
        }

        #endregion
    }
}
