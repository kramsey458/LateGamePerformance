using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Timberborn.SaveSystem;

namespace LateGamePerformance
{
    // One entry of a save file: either bytes that were produced on the game thread, or a writer that may run on
    // any thread because it only reads data nothing else holds.
    internal sealed class SaveEntry
    {
        public string Name;
        public byte[] Bytes;
        public Action<Stream> Write;
    }

    // Turns prepared entries into a save file, away from the game thread. Free of Unity and the game so the tests
    // can drive it, failures included.
    //
    // The file is never written in place. The archive is built in memory exactly as the game builds it (same zip
    // mode, same entry order, same compression level), read back and checked, written to "<name>.timber.saving"
    // next to the target, flushed to the disk, and only then renamed over the target. The game lists saves by the
    // ".timber" extension, so an unfinished file is never offered for loading, and a save that is overwritten
    // stays intact until its replacement is complete (the game itself truncates it first and then writes).
    internal sealed class SaveJob
    {
        public const string TempSuffix = ".saving";

        public readonly List<SaveEntry> Entries = new List<SaveEntry>();
        public string TargetPath;
        public string TempPath;
        public Stream TempStream;
        public object Owner;

        public Exception Failure { get; private set; }
        public long BuildStopwatchTicks { get; private set; }
        public long FileStopwatchTicks { get; private set; }
        public long Bytes { get; private set; }
        public bool IsFinished => _finished.IsSet;

        // Test hooks: the pause between attempts to rename, and the rename itself.
        internal int RetryPauseMilliseconds = 150;
        internal Action<string, string> CommitFile = Commit;

        private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
        // Only ever an archive that passed the check; anything else is built again.
        private MemoryStream _archive;

        public void Start()
        {
            Thread thread = new Thread(Run)
            {
                // Not a background thread: if the game quits, the process stays until the file is complete.
                IsBackground = false,
                Name = "LateGamePerformance save"
            };
            thread.Start();
        }

        public void Wait()
        {
            _finished.Wait();
        }

        internal void Run()
        {
            try
            {
                long started = Stopwatch.GetTimestamp();
                MemoryStream archive = BuildArchive(Entries);
                CheckArchive(archive, Entries);
                _archive = archive;
                long built = Stopwatch.GetTimestamp();
                BuildStopwatchTicks = built - started;

                _archive.Position = 0;
                _archive.CopyTo(TempStream);
                TempStream.Flush();
                (TempStream as FileStream)?.Flush(true);
                TempStream.Dispose();
                TempStream = null;
                long written = new FileInfo(TempPath).Length;
                if (written != _archive.Length)
                {
                    throw new IOException($"{TempPath} is {written} bytes on disk where {_archive.Length} were written");
                }
                Exception last = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        CommitFile(TempPath, TargetPath);
                        last = null;
                        break;
                    }
                    catch (IOException exception)
                    {
                        // Something has the target open (a virus scanner, a cloud sync client). Give it a moment.
                        last = exception;
                        Thread.Sleep(RetryPauseMilliseconds);
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        last = exception;
                        Thread.Sleep(RetryPauseMilliseconds);
                    }
                }
                if (last != null)
                {
                    throw last;
                }
                Bytes = _archive.Length;
                FileStopwatchTicks = Stopwatch.GetTimestamp() - built;
                _archive = null;
                DeleteLeftovers(Path.GetDirectoryName(TargetPath));
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
            finally
            {
                _finished.Set();
            }
        }

        // After a failure on the worker thread: the same save once more, on the calling thread. The entries are still
        // here, so nothing about the game's state is needed, and the archive is the same bytes whichever way it is
        // written. Since 0.4.28 it first goes the worker's way again, into the .saving file and one rename over the
        // target, so a save that is overwritten stays intact until its replacement is complete. If any step of that
        // fails, it is written straight into the target the way the game writes it (truncating it first), as before:
        // that also works where a rename cannot (something that lets the file be written but not replaced) and where
        // the disk only has room for the new save once the old one's space is freed. Throws if that fails as well.
        public void CompleteOnCallingThread()
        {
            try
            {
                TempStream?.Dispose();
            }
            catch (Exception)
            {
                // The worker's temp stream is given up on either way.
            }
            TempStream = null;
            long started = Stopwatch.GetTimestamp();
            if (_archive == null)
            {
                MemoryStream archive = BuildArchive(Entries);
                CheckArchive(archive, Entries);
                _archive = archive;
            }
            long built = Stopwatch.GetTimestamp();
            BuildStopwatchTicks = built - started;
            WrittenInPlace = !TryThroughTempFile();
            if (WrittenInPlace)
            {
                // The temp file's space back first, in case that is what the disk is short of.
                DeleteQuietly(TempPath);
                using (FileStream target = File.Create(TargetPath))
                {
                    _archive.Position = 0;
                    _archive.CopyTo(target);
                    target.Flush(true);
                }
            }
            Bytes = _archive.Length;
            FileStopwatchTicks = Stopwatch.GetTimestamp() - built;
            _archive = null;
            Failure = null;
            DeleteQuietly(TempPath);
        }

        // Whether the last CompleteOnCallingThread had to write straight into the target.
        public bool WrittenInPlace { get; private set; }

        // The archive into the .saving file, flushed and checked on the disk, then renamed over the target once.
        // False, with the target as it was (or, if the rename itself failed halfway, gone), if any step fails.
        private bool TryThroughTempFile()
        {
            if (TempPath == null)
            {
                return false;
            }
            try
            {
                using (FileStream temp = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    _archive.Position = 0;
                    _archive.CopyTo(temp);
                    temp.Flush(true);
                }
                if (new FileInfo(TempPath).Length != _archive.Length)
                {
                    return false;
                }
                CommitFile(TempPath, TargetPath);
                return true;
            }
            catch (Exception)
            {
                // The game's way follows.
                return false;
            }
        }

        // Writes the archive straight into a stream the game opened (a save this mod prepared but does not own).
        public void WriteInto(Stream destination)
        {
            using (MemoryStream archive = BuildArchive(Entries))
            {
                archive.Position = 0;
                archive.CopyTo(destination);
            }
        }

        // SaveWriter.WriteToSaveStream, with the entries already in hand.
        public static MemoryStream BuildArchive(List<SaveEntry> entries)
        {
            MemoryStream memory = new MemoryStream();
            using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Update, true))
            {
                foreach (SaveEntry entry in entries)
                {
                    using (Stream stream = archive.CreateEntry(entry.Name, CompressionLevel.Fastest).Open())
                    {
                        if (entry.Bytes != null)
                        {
                            stream.Write(entry.Bytes, 0, entry.Bytes.Length);
                        }
                        else
                        {
                            entry.Write(stream);
                        }
                    }
                }
            }
            return memory;
        }

        // Reads the finished archive back: the same entries in the same order, every one decompressing to the
        // end, prepared entries to exactly their length, written ones to something.
        public static void CheckArchive(MemoryStream memory, List<SaveEntry> entries)
        {
            memory.Position = 0;
            using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read, true))
            {
                if (archive.Entries.Count != entries.Count)
                {
                    throw new InvalidDataException($"the archive has {archive.Entries.Count} entries where {entries.Count} were written");
                }
                byte[] buffer = new byte[81920];
                for (int i = 0; i < entries.Count; i++)
                {
                    ZipArchiveEntry written = archive.Entries[i];
                    if (written.FullName != entries[i].Name)
                    {
                        throw new InvalidDataException($"entry {i} is '{written.FullName}' where '{entries[i].Name}' was written");
                    }
                    long length = 0;
                    using (Stream stream = written.Open())
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            length += read;
                        }
                    }
                    bool wrong = entries[i].Bytes != null ? length != entries[i].Bytes.Length : length == 0;
                    if (wrong || length != written.Length)
                    {
                        throw new InvalidDataException($"entry '{written.FullName}' reads back as {length} bytes");
                    }
                }
            }
        }

        private static void Commit(string tempPath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                try
                {
                    // One step: there is no moment without a complete file under the target's name.
                    File.Replace(tempPath, targetPath, null);
                    return;
                }
                catch (IOException)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A runtime or file system without Replace: delete and move. The complete new file exists
                    // under its .saving name throughout.
                }
                File.Delete(targetPath);
            }
            File.Move(tempPath, targetPath);
        }

        // .saving files a crash or a power cut left behind in this settlement's folder. Never one in use: a job's
        // own is gone by now, and only one job runs at a time.
        internal static void DeleteLeftovers(string directory)
        {
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*" + TempSuffix))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(10))
                    {
                        DeleteQuietly(path);
                    }
                }
            }
            catch (Exception)
            {
                // Clutter, not a problem.
            }
        }

        internal static void DeleteQuietly(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover temp file is clutter, not a problem; the game does not list it.
            }
        }
    }

    // A save freezes the game for 0.8 s on a fast computer and up to 2.4 s on a slower one in the colony this was
    // measured in, and more than half of that is turning the snapshot into JSON, compressing it and writing the
    // file. None of that needs the game: the snapshot is plain data from the moment it is taken (every value is
    // converted to a number, a string or a nested plain object when it is stored). So for saves the game queues
    // (autosaves and saves from the menu) that second half runs on a worker thread and the game carries on.
    //
    // How it fits into the game's own save, without replacing it:
    //
    //   GameSaver.Save (NOT patched: exception filter, and BeaverBuddies hooks it to move the save to a tick
    //   boundary; all of that keeps working because everything below happens inside it)
    //     Ticker.FinishFullTick                          the game's
    //     SaveWriter.WriteToSaveStream                   replaced: the snapshot is taken and every other entry
    //                                                    (thumbnail, metadata, anything a mod adds) is written,
    //                                                    here, on the game thread, in the game's order. Only the
    //                                                    world entry's JSON is left for later.
    //     GameSaveRepository.CreateSave...               the game's: name validation, settlement directory
    //       FileService.CreateFile                       replaced: opens "<file>.saving", starts the worker,
    //                                                    hands the game an empty stream to "copy" into
    //     onSaveCompleted                                wrapped when the save was queued: the game's callback
    //                                                    (autosave: delete the oldest autosaves; menu: close the
    //                                                    box) runs on the game thread once the file is in place
    //
    // Rules that keep a save safe:
    //   - Only queued saves. The save on exit, BeaverBuddies' rehost save (both "instant": their caller expects
    //     the file on return) and saves to a stream (the game's save benchmark and crash-report save) run as the
    //     game's own code, untouched.
    //   - One job at a time. Any save of any kind first waits for a job still running, and so does anything that
    //     asks the save repository about files (listing, opening, deleting, exists), and quitting the game.
    //   - If the ".saving" file cannot be opened, the game's own code opens the real file and the save completes
    //     the game's way, there and then, errors included.
    //   - If the worker fails, the save is done again on the game thread from the same snapshot: through the
    //     ".saving" file and a rename once more, and if that fails, straight into the file the game's way. If that
    //     fails too, the game's callback does not run (so no old autosave is deleted) and the game gets the same
    //     GameSaverException it would have had.
    //   - If preparing fails, nothing has been written: the feature turns itself off and the game's own code
    //     performs that save in full. If the snapshot was already taken, the game's snapshot runs again, so every
    //     singleton saves its state twice; SaveSnapshot's fallback does the same, and why that is harmless is
    //     written there (CreatePrefix).
    //
    // This changes nothing in the simulation, so it does not matter in multiplayer whether every player has it.
    internal static class BackgroundSave
    {
        private sealed class Token
        {
            public object Reference;
            public Action Callback;
            public SaveJob Job;
            public bool GameThreadPartFinished;
            public bool Abandoned;
            public long PreparedStopwatchTicks;

            // What the game calls at the end of GameSaver.Save in place of the callback it was given.
            public void SaveReturned()
            {
                if (Job == null)
                {
                    // This save went the game's own way; the file is complete.
                    Forget(this);
                    Callback?.Invoke();
                    return;
                }
                GameThreadPartFinished = true;
            }
        }

        private static Func<object, object> _writersOf;
        private static Type _worldWriterType;
        private static Func<object, object> _worldFactoryOf;
        private static Func<object, object> _worldSerializerOf;
        private static Func<object, object> _createWorld;
        private static Action<object, Stream, object> _writeWorld;
        private static ConstructorInfo _saverException;

        private static bool _active;
        private static int _mainThreadId;
        private static int _instantDepth;
        private static Token _queued;
        private static SaveJob _prepared;
        private static Token _preparedFor;
        private static bool _insideCreateSave;
        private static bool _callerReported;
        private static readonly List<Token> Running = new List<Token>();

        // Swapped by the tests, which cannot be called by GameSaver.Save.
        internal static Func<string> CallerProbe = () => CallerOfWriter(new StackTrace(false));

        internal static Action<Action> OnQuitting = handler => UnityEngine.Application.quitting += handler;

        public static Feature CreateFeature()
        {
            Type self = typeof(BackgroundSave);
            Feature feature = new Feature { Name = "BackgroundSave" };
            Func<Type> saver = () => Reflect.GameType("Timberborn.GameSaveRuntimeSystem.GameSaver");
            Func<Type> repository = () => Reflect.GameType("Timberborn.GameSaveRepositorySystem.GameSaveRepository");
            feature.Patches.Add(new PatchSpec
            {
                Name = "SaveWriter.WriteToSaveStream",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return AccessTools.Method(typeof(SaveWriter), "WriteToSaveStream");
                },
                Prefix = Reflect.Own(self, nameof(WritePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.QueueSave",
                Required = true,
                Target = () => AccessTools.Method(saver(), "QueueSave"),
                Prefix = Reflect.Own(self, nameof(QueuePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.QueueSaveSkippingNameValidation",
                Required = true,
                Target = () => AccessTools.Method(saver(), "QueueSaveSkippingNameValidation"),
                Prefix = Reflect.Own(self, nameof(QueuePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.SaveQueued",
                Required = true,
                Target = () => AccessTools.Method(saver(), "SaveQueued"),
                Prefix = Reflect.Own(self, nameof(EveryFramePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.SaveInstantlySkippingNameValidation",
                Required = true,
                Target = () => AccessTools.Method(saver(), "SaveInstantlySkippingNameValidation"),
                Prefix = Reflect.Own(self, nameof(InstantPrefix)),
                Finalizer = Reflect.Own(self, nameof(InstantFinalizer))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaveRepository.CreateSaveSkippingNameValidation",
                Required = true,
                Target = () => AccessTools.Method(repository(), "CreateSaveSkippingNameValidation"),
                Prefix = Reflect.Own(self, nameof(CreateSavePrefix)),
                Postfix = Reflect.Own(self, nameof(CreateSavePostfix)),
                Finalizer = Reflect.Own(self, nameof(CreateSaveFinalizer))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "FileService.CreateFile",
                Required = true,
                Target = () => Reflect.Method("Timberborn.FileSystem.FileService", "CreateFile"),
                Prefix = Reflect.Own(self, nameof(CreateFilePrefix))
            });
            // Whoever asks the repository about files finds every save complete.
            foreach (string name in new[] { "SaveNameToFileName", "GetSaves" })
            {
                string method = name;
                feature.Patches.Add(new PatchSpec
                {
                    Name = "GameSaveRepository." + method,
                    Required = true,
                    Target = () => AccessTools.Method(repository(), method),
                    Prefix = Reflect.Own(self, nameof(WaitPrefix))
                });
            }
            return feature;
        }

        public static void Activate()
        {
            _active = true;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                OnQuitting(WaitForRunningJobs);
            }
            catch (Exception exception)
            {
                Log.Warning("BackgroundSave: could not register for the game quitting: " + exception.Message);
            }
        }

        public static bool IsActive => _active;

        // A new game scene: callbacks of the scene that is gone must not run in this one. Their files are
        // finished first; all that is skipped is that scene's tidying up (closing its box, pruning autosaves).
        public static void SceneCreated()
        {
            try
            {
                WaitForRunningJobs();
                foreach (Token token in Running)
                {
                    token.Abandoned = true;
                }
                FinishJobs();
                _queued = null;
                _prepared = null;
                _preparedFor = null;
                _instantDepth = 0;
                _insideCreateSave = false;
            }
            catch (Exception exception)
            {
                Log.Warning("BackgroundSave: " + exception);
            }
        }

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            _writersOf = Reflect.FieldGetter<object>(typeof(SaveWriter), "_saveEntryWriters");
            _worldWriterType = Reflect.GameType("Timberborn.WorldPersistence.WorldEntryWriter");
            Type factory = Reflect.GameType("Timberborn.WorldPersistence.SerializedWorldFactory");
            Type serializer = Reflect.GameType("Timberborn.WorldSerialization.WorldSerializer");
            Type world = Reflect.GameType("Timberborn.WorldSerialization.SerializedWorld");
            _worldFactoryOf = Reflect.FieldGetter<object>(_worldWriterType, "_serializedWorldFactory");
            _worldSerializerOf = Reflect.FieldGetter<object>(_worldWriterType, "_worldSerializer");
            MethodInfo create = AccessTools.Method(factory, "Create", Type.EmptyTypes);
            MethodInfo write = AccessTools.Method(serializer, "WriteToSaveEntryStream", new[] { typeof(Stream), world });
            if (create == null || create.ReturnType != world || write == null)
            {
                throw new MissingMethodException("SerializedWorldFactory.Create() / WorldSerializer.WriteToSaveEntryStream");
            }
            _createWorld = Reflect.InstanceCall<Func<object, object>>(create);
            _writeWorld = Reflect.InstanceCall<Action<object, Stream, object>>(write);

            // The world entry writer must be exactly "snapshot, then serialize"; this feature splits it there.
            int fields = _worldWriterType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                                                    BindingFlags.DeclaredOnly).Length;
            if (fields != 2)
            {
                throw new MissingMemberException($"WorldEntryWriter has {fields} fields where 2 were expected");
            }
            Type exception = Reflect.GameType("Timberborn.GameSaveRuntimeSystem.GameSaverException");
            _saverException = exception.GetConstructor(new[] { typeof(string), typeof(Exception) });
            if (_saverException == null)
            {
                throw new MissingMethodException("GameSaverException(string, Exception)");
            }
        }

        // ReSharper disable InconsistentNaming
        internal static void QueuePrefix(object saveReference, ref Action onSaveCompleted)
        {
            if (!_active || !OnGameThread())
            {
                return;
            }
            try
            {
                // The game keeps one queued save; a second one replaces the first, callback and all.
                Token token = new Token { Reference = saveReference, Callback = onSaveCompleted };
                _queued = token;
                onSaveCompleted = token.SaveReturned;
            }
            catch (Exception exception)
            {
                Log.Warning("BackgroundSave: " + exception);
            }
        }

        // GameSaver.SaveQueued runs every frame, late. Finished jobs hand over to the game's callback here.
        internal static void EveryFramePrefix()
        {
            // A prepared save nobody asked a file for (the game refused the name): let go of the snapshot.
            _prepared = null;
            _preparedFor = null;
            if (Running.Count > 0 && OnGameThread())
            {
                FinishJobs();
            }
        }

        internal static void InstantPrefix()
        {
            _instantDepth++;
            WaitForRunningJobs();
        }

        internal static void InstantFinalizer()
        {
            if (_instantDepth > 0)
            {
                _instantDepth--;
            }
        }

        internal static void WaitPrefix()
        {
            WaitForRunningJobs();
        }

        [HarmonyPriority(Priority.Last)]
        internal static bool WritePrefix(object __instance, Stream saveStream, bool leaveOpen)
        {
            // Every save, of every kind, starts with the previous one on disk.
            WaitForRunningJobs();
            Token token = _queued;
            _prepared = null;
            _preparedFor = null;
            // GameSaver.Save is the one caller that passes an empty MemoryStream and keeps it open.
            if (!_active || !leaveOpen || _instantDepth > 0 || token == null || token.Job != null || !OnGameThread() ||
                !(saveStream is MemoryStream memory) || memory.Length != 0)
            {
                return true;
            }
            try
            {
                // Leaving the stream empty is only right if the caller is GameSaver.Save, which asks the
                // repository for the file next. Anyone else gets the game's own writer.
                string caller = CallerProbe();
                if (!IsGameSaverSave(caller))
                {
                    if (!_callerReported)
                    {
                        _callerReported = true;
                        Log.Info($"BackgroundSave: a queued save is pending but the writer was called by '{caller}', " +
                                 "not by GameSaver.Save; the game writes this one itself.");
                    }
                    return true;
                }
                long started = Stopwatch.GetTimestamp();
                SaveJob job = Prepare(__instance);
                if (job == null)
                {
                    return true;
                }
                token.PreparedStopwatchTicks = Stopwatch.GetTimestamp() - started;
                _prepared = job;
                _preparedFor = token;
                SaveTiming.Note("world JSON, compression and the file write follow on a worker thread");
                return false;
            }
            catch (Exception exception)
            {
                // Nothing has been written anywhere. What ran so far only read the game's state, and the game's
                // own writer now runs all of it again.
                _active = false;
                Log.Warning("BackgroundSave failed while preparing a save and turned itself off for this session; " +
                            "the game performs this save itself: " + exception);
                return true;
            }
        }

        internal static void CreateSavePrefix(object saveReference)
        {
            // Only the save that was queued goes to the worker. Any other prepared save (an instant one that
            // BeaverBuddies moved to a tick boundary, say) gets its file the game's way, in the postfix.
            Token token = _preparedFor;
            _insideCreateSave = _prepared != null && token != null && ReferenceEquals(token, _queued) &&
                                Equals(token.Reference, saveReference);
        }

        [HarmonyPriority(Priority.Last)]
        internal static bool CreateFilePrefix(string fileName, ref Stream __result)
        {
            if (!_insideCreateSave || _prepared == null)
            {
                return true;
            }
            _insideCreateSave = false;
            SaveJob job = _prepared;
            Stream temp;
            try
            {
                job.TargetPath = fileName;
                job.TempPath = fileName + SaveJob.TempSuffix;
                temp = new FileStream(job.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception exception)
            {
                // The game opens the real file itself (or fails its own way) and the save completes in it.
                Log.Warning($"BackgroundSave: could not open {fileName}{SaveJob.TempSuffix} ({exception.Message}); " +
                            "this save is written the game's way.");
                return true;
            }
            job.TempStream = temp;
            job.Owner = _preparedFor;
            _preparedFor.Job = job;
            Running.Add(_preparedFor);
            _prepared = null;
            _preparedFor = null;
            job.Start();
            __result = new MemoryStream();
            return false;
        }

        // Not wrapped in a try: an error writing the file belongs to the game here, exactly as if its own copy
        // into this stream had failed (GameSaver.Save turns it into a GameSaverException).
        //
        // Whenever a prepared save is still here after the game has its file stream, for whatever reason (not the
        // queued save, the ".saving" file could not be opened, another mod supplies the file service), it is
        // written into that stream now. A prepared save never ends as an empty file.
        internal static void CreateSavePostfix(Stream __result)
        {
            SaveJob job = _prepared;
            if (job == null)
            {
                return;
            }
            _prepared = null;
            _preparedFor = null;
            job.WriteInto(__result);
        }

        internal static void CreateSaveFinalizer()
        {
            _insideCreateSave = false;
            _prepared = null;
            _preparedFor = null;
        }
        // ReSharper restore InconsistentNaming

        // SaveWriter.WriteToSaveStream's loop, on the game thread, with the world entry's second half set aside.
        private static SaveJob Prepare(object saveWriter)
        {
            SaveJob job = new SaveJob();
            bool worldFound = false;
            foreach (object item in (IEnumerable)_writersOf(saveWriter))
            {
                ISaveEntryWriter writer = (ISaveEntryWriter)item;
                if (writer is IOptionalSaveEntryWriter optional && !optional.ShouldWrite)
                {
                    continue;
                }
                if (writer.GetType() == _worldWriterType)
                {
                    object serializer = _worldSerializerOf(writer);
                    object world = _createWorld(_worldFactoryOf(writer));
                    if (serializer == null || world == null)
                    {
                        return null;
                    }
                    job.Entries.Add(new SaveEntry { Name = writer.EntryName, Write = stream => _writeWorld(serializer, stream, world) });
                    worldFound = true;
                }
                else
                {
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        writer.WriteToSaveEntryStream(buffer);
                        job.Entries.Add(new SaveEntry { Name = writer.EntryName, Bytes = buffer.ToArray() });
                    }
                }
            }
            // Without the game's world entry writer there is nothing to move; the game's own loop runs.
            return worldFound ? job : null;
        }

        // The method that called SaveWriter.WriteToSaveStream: the first frame that is neither this class nor the
        // (patched) writer itself.
        private static string CallerOfWriter(StackTrace trace)
        {
            for (int i = 0; i < trace.FrameCount && i < 12; i++)
            {
                MethodBase method = trace.GetFrame(i)?.GetMethod();
                string type = method?.DeclaringType?.FullName;
                if (method == null || (type != null && type.StartsWith(typeof(BackgroundSave).FullName, StringComparison.Ordinal)))
                {
                    continue;
                }
                string name = (method.DeclaringType != null ? method.DeclaringType.FullName + "::" : "") + method.Name;
                if (name.Contains("WriteToSaveStream"))
                {
                    continue;
                }
                return name;
            }
            return "";
        }

        // GameSaver.Save itself, or a detour's copy of it (BeaverBuddies hooks Save; the copy is a dynamic method
        // that carries the original's full name). Not SaveQueued, SaveInstantly..., SaveWithoutFinishingTick.
        internal static bool IsGameSaverSave(string caller)
        {
            return caller != null && System.Text.RegularExpressions.Regex.IsMatch(caller,
                @"Timberborn\.GameSaveRuntimeSystem\.GameSaver(::|\.|:)Save(?![A-Za-z])");
        }

        internal static bool OnGameThread()
        {
            return _mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static void Forget(Token token)
        {
            if (ReferenceEquals(_queued, token))
            {
                _queued = null;
            }
        }

        internal static void WaitForRunningJobs()
        {
            // Only the game thread starts jobs, so only it has anything to wait for; the worker itself never
            // comes through here.
            if (Running.Count == 0 || !OnGameThread())
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            foreach (Token token in Running)
            {
                token.Job.Wait();
            }
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 1)
            {
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "BackgroundSave: the game thread waited {0:0} ms for a save still being written.", ms));
            }
        }

        // Game thread. For every job that is done and whose GameSaver.Save has returned: on failure, the save
        // once more right here; then the game's callback.
        private static void FinishJobs()
        {
            for (int i = 0; i < Running.Count; i++)
            {
                Token token = Running[i];
                if (!token.Job.IsFinished || !(token.GameThreadPartFinished || token.Abandoned))
                {
                    continue;
                }
                Running.RemoveAt(i--);
                Forget(token);
                SaveJob job = token.Job;
                if (job.Failure != null)
                {
                    Exception first = job.Failure;
                    Log.Warning($"BackgroundSave: writing {job.TargetPath} on the worker thread failed ({first.Message}). " +
                                "Saving again on the game thread from the same snapshot.");
                    try
                    {
                        job.CompleteOnCallingThread();
                    }
                    catch (Exception second)
                    {
                        SaveJob.DeleteQuietly(job.TempPath);
                        _active = false;
                        Log.Warning($"BackgroundSave: SAVE FAILED: {job.TargetPath} could not be written ({second}). " +
                                    "The save's completion was not reported to the game, so no older autosave was " +
                                    "deleted because of it. Background saving is off for the rest of this session.");
                        if (token.Abandoned)
                        {
                            continue;
                        }
                        // What the game would have thrown out of this same method.
                        throw (Exception)_saverException.Invoke(new object[] { second.Message, second });
                    }
                    // The worker failed once; do not rely on it again this session.
                    _active = false;
                    Log.Warning("BackgroundSave: the save was written on the game thread" +
                                (job.WrittenInPlace ? ", straight into the file" : ", through its .saving file and a rename") +
                                ". Background saving is off for the rest of this session.");
                }
                Log.Info(Summary(token));
                if (!token.Abandoned)
                {
                    // Exceptions from the game's callback go where they went before: out of SaveQueued.
                    token.Callback?.Invoke();
                }
            }
        }

        private static string Summary(Token token)
        {
            SaveJob job = token.Job;
            double ms = 1000.0 / Stopwatch.Frequency;
            return string.Format(CultureInfo.InvariantCulture,
                "BackgroundSave: {0} written ({1:0.0} MB). Off the game thread: world JSON, compression and check " +
                "{2:0} ms, file {3:0} ms. On the game thread: snapshot and the other entries {4:0} ms.",
                Path.GetFileName(job.TargetPath), job.Bytes / 1048576.0, job.BuildStopwatchTicks * ms,
                job.FileStopwatchTicks * ms, token.PreparedStopwatchTicks * ms);
        }

        // For the tests.
        internal static void ResetForTests()
        {
            _active = false;
            _mainThreadId = 0;
            _instantDepth = 0;
            _queued = null;
            _prepared = null;
            _preparedFor = null;
            _insideCreateSave = false;
            _callerReported = false;
            Running.Clear();
        }

        internal static int RunningCount => Running.Count;

        // The snapshot needs a loaded game; the tests supply one they built.
        internal static void ReplaceSnapshotForTests(Func<object, object> createWorld)
        {
            _createWorld = createWorld;
        }
    }
}
