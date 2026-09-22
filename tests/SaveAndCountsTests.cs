using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LateGamePerformance;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SaveSystem;
using Timberborn.SerializationSystem;
using Timberborn.WorldSerialization;

// Background saving and district counting, against the installed game's real classes where a loaded game is not
// needed for them.
//
// Saving: the file writer on its own (every failure it has an answer for), the game's real WorldSerializer run on
// a worker thread against a world built here, and the whole chain of hooks in the order GameSaver.Save calls them,
// with a real SaveWriter, the real WorldEntryWriter type and real files.
//
// Counting: real Inventory objects with the game's own capacity rules, one counter filled by the game's
// UpdateCounters and one by the mod, compared through the game's public GetResourceCount.
internal static class SaveAndCountsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "lgp-save-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RunSaveJob(directory, check);
            RunWorldOnWorkerThread(check);
            RunSaveFlow(directory, check);
        }
        finally
        {
            BackgroundSave.ResetForTests();
            Directory.Delete(directory, true);
        }
        RunDistrictCounts(check);
    }

    // ------------------------------------------------------------------ the file writer

    private static List<SaveEntry> Entries(string world, Action beforeWorld = null)
    {
        return new List<SaveEntry>
        {
            new SaveEntry
            {
                Name = "world.json",
                Write = stream =>
                {
                    beforeWorld?.Invoke();
                    byte[] bytes = Encoding.UTF8.GetBytes(world);
                    stream.Write(bytes, 0, bytes.Length);
                }
            },
            new SaveEntry { Name = "save_thumbnail.jpg", Bytes = new byte[] { 1, 2, 3, 4, 5 } },
            new SaveEntry { Name = "save_metadata.json", Bytes = Encoding.UTF8.GetBytes("{\"a\":1}") },
            new SaveEntry { Name = "empty.bin", Bytes = new byte[0] }
        };
    }

    private static SaveJob Job(string target, List<SaveEntry> entries)
    {
        SaveJob job = new SaveJob { TargetPath = target, TempPath = target + SaveJob.TempSuffix, RetryPauseMilliseconds = 1 };
        job.Entries.AddRange(entries);
        job.TempStream = new FileStream(job.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        return job;
    }

    private static Dictionary<string, string> ReadZip(string path, out List<string> order)
    {
        Dictionary<string, string> contents = new Dictionary<string, string>();
        order = new List<string>();
        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using (MemoryStream buffer = new MemoryStream())
                using (Stream stream = entry.Open())
                {
                    stream.CopyTo(buffer);
                    contents[entry.FullName] = Convert.ToBase64String(buffer.ToArray());
                    order.Add(entry.FullName);
                }
            }
        }
        return contents;
    }

    private static string WorldOf(string path)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(ReadZip(path, out _)["world.json"]));
    }

    private static void RunSaveJob(string directory, Action<bool, string> check)
    {
        string world = string.Concat(Enumerable.Repeat("{\"Entities\":[1,2,3]}", 20000));

        // The plain case, on its own thread, compared with what the game's writer produces for the same entries.
        string target = Path.Combine(directory, "plain.timber");
        SaveJob job = Job(target, Entries(world));
        job.Start();
        job.Wait();
        check(job.Failure == null && job.IsFinished, "save job: finishes without a failure (" + job.Failure?.Message + ")");
        check(File.Exists(target) && !File.Exists(target + SaveJob.TempSuffix), "save job: the target exists and the .saving file is gone");
        string gameWay = Path.Combine(directory, "gameway.timber");
        using (MemoryStream memory = new MemoryStream())
        {
            // SaveWriter.WriteToSaveStream, literally.
            using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Update, true))
            {
                foreach (SaveEntry entry in Entries(world))
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
            File.WriteAllBytes(gameWay, memory.ToArray());
        }
        Dictionary<string, string> mine = ReadZip(target, out List<string> myOrder);
        Dictionary<string, string> games = ReadZip(gameWay, out List<string> gameOrder);
        check(myOrder.SequenceEqual(gameOrder) && myOrder.All(name => mine[name] == games[name]),
            "save job: same entries, same order, same contents as the game's writer");
        check(new FileInfo(target).Length == new FileInfo(gameWay).Length && job.Bytes == new FileInfo(target).Length,
            $"save job: the file is the same size as the game's ({new FileInfo(target).Length} bytes)");

        // A .saving file left by a crash long ago is cleared away by the next save in that folder; a fresh one
        // (it could be anything) is not touched.
        string stale = Path.Combine(directory, "crashed.timber" + SaveJob.TempSuffix);
        string fresh = Path.Combine(directory, "fresh.timber" + SaveJob.TempSuffix);
        File.WriteAllText(stale, "half a save");
        File.WriteAllText(fresh, "half a save");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        Job(Path.Combine(directory, "tidy.timber"), Entries("tidy")).Run();
        check(!File.Exists(stale) && File.Exists(fresh), "save job: old .saving leftovers are cleared, recent ones are left alone");
        File.Delete(fresh);

        // Overwriting: the old save is intact until the new one is complete, and replaced after.
        string overwritten = Path.Combine(directory, "overwrite.timber");
        Job(overwritten, Entries("old world")).Run();
        bool oldIntactWhileWriting = false;
        job = Job(overwritten, Entries("new world", () => oldIntactWhileWriting = WorldOf(overwritten) == "old world"));
        job.Run();
        check(job.Failure == null && oldIntactWhileWriting && WorldOf(overwritten) == "new world",
            "save job: an overwritten save stays readable until its replacement is complete");

        // The rename is refused twice (a scanner has the file), then goes through.
        int attempts = 0;
        job = Job(overwritten, Entries("third world"));
        Action<string, string> commit = job.CommitFile;
        job.CommitFile = (temp, final) =>
        {
            if (++attempts < 3)
            {
                throw new IOException("in use");
            }
            commit(temp, final);
        };
        job.Run();
        check(job.Failure == null && attempts == 3 && WorldOf(overwritten) == "third world", "save job: a refused rename is tried again");

        // The rename never goes through: the job fails, the old save is untouched, and the second attempt on
        // the calling thread writes the save the game's way from the same entries.
        job = Job(overwritten, Entries("fourth world"));
        job.CommitFile = (temp, final) => throw new UnauthorizedAccessException("denied");
        job.Run();
        check(job.Failure is UnauthorizedAccessException && WorldOf(overwritten) == "third world",
            "save job: a failed job reports it and leaves the old save as it was");
        job.CompleteOnCallingThread();
        check(job.Failure == null && WorldOf(overwritten) == "fourth world" && !File.Exists(job.TempPath) && job.WrittenInPlace,
            "save job: the second attempt, whose rename is refused too, writes the save straight into the file and removes the .saving file");

        // The world writer throws on the worker: nothing is committed; on the calling thread it works, through the
        // .saving file and a rename, so the old save is intact until the new one is complete (0.4.28).
        int calls = 0;
        job = Job(overwritten, Entries("fifth world", () =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("not on this thread");
            }
        }));
        job.Run();
        check(job.Failure is InvalidOperationException && WorldOf(overwritten) == "fourth world",
            "save job: a writer that throws leaves the old save as it was");
        bool oldIntactAtRename = false, completeAtRename = false;
        Action<string, string> realCommit = job.CommitFile;
        job.CommitFile = (temp, final) =>
        {
            oldIntactAtRename = WorldOf(final) == "fourth world";
            completeAtRename = WorldOf(temp) == "fifth world";
            realCommit(temp, final);
        };
        job.CompleteOnCallingThread();
        check(WorldOf(overwritten) == "fifth world" && oldIntactAtRename && completeAtRename && !job.WrittenInPlace &&
              !File.Exists(job.TempPath),
            "save job: and the second attempt runs the writer again, keeping the old save intact until the new one is complete on the disk");

        // The .saving file cannot be written on the second attempt (its name is taken by a directory now): the save
        // goes straight into the file, the game's way.
        job = Job(overwritten, Entries("sixth world"));
        job.CommitFile = (temp, final) => throw new IOException("in use");
        job.Run();
        job.TempStream?.Dispose();
        File.Delete(job.TempPath);
        Directory.CreateDirectory(job.TempPath);
        job.CompleteOnCallingThread();
        check(job.Failure == null && WorldOf(overwritten) == "sixth world" && job.WrittenInPlace,
            "save job: when the .saving file cannot be written again, the second attempt writes straight into the file");
        Directory.Delete(job.TempPath);

        // A world entry that comes out empty is refused before anything is renamed.
        job = Job(overwritten, Entries(""));
        job.Run();
        check(job.Failure is InvalidDataException && WorldOf(overwritten) == "sixth world",
            "save job: an empty world entry is refused and the old save stays");
        bool threw = false;
        try
        {
            job.CompleteOnCallingThread();
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        check(threw && WorldOf(overwritten) == "sixth world", "save job: the second attempt refuses it too, before touching the file");
        SaveJob.DeleteQuietly(job.TempPath);

        // The check itself.
        List<SaveEntry> entries = Entries("x");
        MemoryStream good = SaveJob.BuildArchive(entries);
        SaveJob.CheckArchive(good, entries);
        entries[1].Bytes = new byte[] { 1, 2, 3 };
        threw = false;
        try
        {
            SaveJob.CheckArchive(good, entries);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        check(threw, "save job: the check notices an entry of the wrong length");
    }

    // ------------------------------------------------------------------ the game's serializer off the main thread

    private static SerializedWorld BuildWorld(int entities)
    {
        SerializedWorld world = new SerializedWorld(Timberborn.Versioning.Version.Create("1.1.2.4-test"));
        SerializedObject singleton = world.GetOrAddSingleton("DayNightCycle");
        singleton.Set("DayNumber", 412);
        singleton.Set("DayProgress", 0.35f);
        for (int i = 0; i < entities; i++)
        {
            SerializedEntity entity = new SerializedEntity(new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10), "Beaver" + i % 7);
            SerializedObject component = entity.GetOrAddComponent("Inventory").Value;
            component.Set("Name", "inventory " + i);
            component.Set("Amount", i * 3);
            component.Set("Open", i % 2 == 0);
            component.Set("Wellbeing", i / 7f);
            SerializedObject position = new SerializedObject();
            position.Set("X", i % 256);
            position.Set("Y", i / 256);
            component.Set("Position", position);
            component.SetArray("Goods", new[] { "Log", "Plank", "Water" });
            world.AddEntity(entity);
        }
        return world;
    }

    private static string WithoutTimestamp(byte[] json)
    {
        return Regex.Replace(Encoding.UTF8.GetString(json), "\"Timestamp\":\\s*\"[^\"]*\"", "\"Timestamp\":\"\"");
    }

    private static void RunWorldOnWorkerThread(Action<bool, string> check)
    {
        WorldSerializer serializer = new WorldSerializer(new SerializedObjectReaderWriter(new JsonMerger()));
        SerializedWorld world = BuildWorld(4000);
        byte[] here;
        using (MemoryStream stream = new MemoryStream())
        {
            serializer.WriteToSaveEntryStream(stream, world);
            here = stream.ToArray();
        }
        byte[] there = null;
        Exception failure = null;
        Thread worker = new Thread(() =>
        {
            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteToSaveEntryStream(stream, world);
                    there = stream.ToArray();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        worker.Join();
        check(failure == null && there != null && WithoutTimestamp(here) == WithoutTimestamp(there),
            $"world JSON: the game's WorldSerializer writes the same {here.Length} bytes on a worker thread as on this one");

        // Through the job and back in through the game's own reader.
        List<SaveEntry> entries = new List<SaveEntry>
        {
            new SaveEntry { Name = serializer.EntryName, Write = stream => serializer.WriteToSaveEntryStream(stream, world) }
        };
        using (MemoryStream archive = SaveJob.BuildArchive(entries))
        {
            SaveJob.CheckArchive(archive, entries);
            archive.Position = 0;
            using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Read, true))
            using (Stream stream = zip.GetEntry(serializer.EntryName).Open())
            {
                SerializedWorld back = serializer.ReadFromSaveEntryStream(stream);
                List<SerializedEntity> before = world.Entities().ToList();
                List<SerializedEntity> after = back.Entities().ToList();
                bool same = before.Count == after.Count;
                for (int i = 0; same && i < before.Count; i++)
                {
                    same = before[i].Id == after[i].Id && before[i].TemplateName == after[i].TemplateName &&
                           before[i].GetComponent("Inventory").Value.Get<string>("Name") ==
                           after[i].GetComponent("Inventory").Value.Get<string>("Name") &&
                           before[i].GetComponent("Inventory").Value.Get<int>("Amount") ==
                           after[i].GetComponent("Inventory").Value.Get<int>("Amount");
                }
                check(same && back.GetSingleton("DayNightCycle").Get<int>("DayNumber") == 412,
                    "world JSON: the game's reader loads what the job wrote, every entity in order");
            }
        }
    }

    // ------------------------------------------------------------------ the hooks, in GameSaver.Save's order

    private sealed class FakeEntryWriter : ISaveEntryWriter
    {
        public string Name;
        public byte[] Bytes;
        public int Calls;
        public bool Throw;
        public string EntryName => Name;

        public void WriteToSaveEntryStream(Stream entryStream)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("entry writer failed");
            }
            entryStream.Write(Bytes, 0, Bytes.Length);
        }
    }

    private sealed class OptionalEntryWriter : IOptionalSaveEntryWriter
    {
        public int Calls;
        public bool ShouldWrite => false;
        public string EntryName => "optional.bin";

        public void WriteToSaveEntryStream(Stream entryStream)
        {
            Calls++;
        }
    }

    private sealed class Flow
    {
        public SaveWriter Writer;
        public FakeEntryWriter Thumbnail;
        public OptionalEntryWriter Optional;
        public int Snapshots;
        public string Directory;
        public string Caller = "Timberborn.GameSaveRuntimeSystem.GameSaver::Save";

        // Stands in for SaveWriter.WriteToSaveStream where the hook leaves the save to the game: the real one
        // needs a loaded game for its snapshot.
        public SerializedWorld World;
        public int GameWrites;

        private void GamesWriter(Stream stream)
        {
            GameWrites++;
            WorldSerializer serializer = new WorldSerializer(new SerializedObjectReaderWriter(new JsonMerger()));
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, true))
            {
                using (Stream entry = archive.CreateEntry("world.json", CompressionLevel.Fastest).Open())
                {
                    serializer.WriteToSaveEntryStream(entry, World);
                }
                using (Stream entry = archive.CreateEntry("save_thumbnail.jpg", CompressionLevel.Fastest).Open())
                {
                    entry.Write(new byte[] { 9, 8, 7 }, 0, 3);
                }
            }
        }

        // What GameSaver.Save does, with the hooks called where Harmony would call them. Returns the path.
        // fileService false: some other IFileService is in place, so FileService.CreateFile is never reached.
        public string Save(string name, object reference, Action callback, bool queued, bool fileService = true,
            bool instant = false)
        {
            Action onSaveCompleted = callback;
            if (queued)
            {
                BackgroundSave.QueuePrefix(reference, ref onSaveCompleted);
                BackgroundSave.EveryFramePrefix();
            }
            if (instant)
            {
                BackgroundSave.InstantPrefix();
            }
            string path = Path.Combine(Directory, name + ".timber");
            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    if (BackgroundSave.WritePrefix(Writer, memory, true))
                    {
                        GamesWriter(memory);
                    }
                    Stream destination = null;
                    try
                    {
                        BackgroundSave.CreateSavePrefix(reference);
                        try
                        {
                            BackgroundSave.WaitPrefix();   // SaveNameToFileName
                            if (!fileService || BackgroundSave.CreateFilePrefix(path, ref destination))
                            {
                                destination = File.Create(path);
                            }
                            BackgroundSave.CreateSavePostfix(destination);
                        }
                        finally
                        {
                            BackgroundSave.CreateSaveFinalizer();
                        }
                        memory.Position = 0;
                        memory.CopyTo(destination);
                        onSaveCompleted?.Invoke();
                    }
                    finally
                    {
                        destination?.Dispose();
                    }
                }
            }
            finally
            {
                if (instant)
                {
                    BackgroundSave.InstantFinalizer();
                }
            }
            return path;
        }
    }

    private static Flow NewFlow(string directory, SerializedWorld world)
    {
        BackgroundSave.ResetForTests();
        BackgroundSave.BindAccessors();
        Flow flow = new Flow { Directory = directory, World = world };
        BackgroundSave.ReplaceSnapshotForTests(factory =>
        {
            flow.Snapshots++;
            return world;
        });
        BackgroundSave.CallerProbe = () => flow.Caller;
        BackgroundSave.OnQuitting = handler => { };
        BackgroundSave.Activate();

        Assembly persistence = Assembly.Load("Timberborn.WorldPersistence");
        Type worldWriterType = persistence.GetType("Timberborn.WorldPersistence.WorldEntryWriter", true);
        object worldWriter = RuntimeHelpers.GetUninitializedObject(worldWriterType);
        worldWriterType.GetField("_serializedWorldFactory", Any).SetValue(worldWriter,
            RuntimeHelpers.GetUninitializedObject(persistence.GetType("Timberborn.WorldPersistence.SerializedWorldFactory", true)));
        worldWriterType.GetField("_worldSerializer", Any).SetValue(worldWriter,
            new WorldSerializer(new SerializedObjectReaderWriter(new JsonMerger())));
        flow.Thumbnail = new FakeEntryWriter { Name = "save_thumbnail.jpg", Bytes = new byte[] { 9, 8, 7 } };
        flow.Optional = new OptionalEntryWriter();
        flow.Writer = new SaveWriter(new[] { (ISaveEntryWriter)worldWriter, flow.Thumbnail, flow.Optional });
        return flow;
    }

    private static bool IsCompleteSave(string path, int entities)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        WorldSerializer serializer = new WorldSerializer(new SerializedObjectReaderWriter(new JsonMerger()));
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count != 2 || zip.Entries[0].FullName != "world.json" || zip.Entries[1].FullName != "save_thumbnail.jpg" ||
                zip.Entries[1].Length != 3)
            {
                return false;
            }
            using (Stream stream = zip.Entries[0].Open())
            {
                return serializer.ReadFromSaveEntryStream(stream).Entities().Count() == entities;
            }
        }
    }

    private static void WaitUntilIdle()
    {
        // What the game does every frame.
        for (int i = 0; i < 2000 && BackgroundSave.RunningCount > 0; i++)
        {
            BackgroundSave.EveryFramePrefix();
            Thread.Sleep(5);
        }
    }

    private static void RunSaveFlow(string directory, Action<bool, string> check)
    {
        const int entities = 1500;
        SerializedWorld world = BuildWorld(entities);
        Flow flow = NewFlow(directory, world);

        check(BackgroundSave.IsGameSaverSave("Timberborn.GameSaveRuntimeSystem.GameSaver::Save") &&
              BackgroundSave.IsGameSaverSave("DMD<System.Void Timberborn.GameSaveRuntimeSystem.GameSaver::Save(QueuedSave)>") &&
              BackgroundSave.IsGameSaverSave("Trampoline<Timberborn.GameSaveRuntimeSystem.GameSaver.Save>") &&
              !BackgroundSave.IsGameSaverSave("Timberborn.GameSaveRuntimeSystem.GameSaver::SaveWithoutFinishingTick") &&
              !BackgroundSave.IsGameSaverSave("Timberborn.GameSaveRuntimeSystem.GameSaver::SaveQueued") &&
              !BackgroundSave.IsGameSaverSave("SomeMod.Hasher::Save") && !BackgroundSave.IsGameSaverSave(""),
            "save flow: only GameSaver.Save, or a detour's copy of it, counts as the caller");

        // A queued save: the callback runs on this thread, and only once the file is complete.
        object reference = new object();
        string path = null;
        bool completeAtCallback = false;
        int callbackThread = 0, callbacks = 0;
        path = flow.Save("autosave", reference, () =>
        {
            callbacks++;
            callbackThread = Environment.CurrentManagedThreadId;
            completeAtCallback = IsCompleteSave(Path.Combine(directory, "autosave.timber"), entities);
        }, true);
        check(callbacks == 0 || completeAtCallback, "save flow: GameSaver.Save returning does not run the game's callback early");
        WaitUntilIdle();
        check(callbacks == 1 && completeAtCallback && callbackThread == Environment.CurrentManagedThreadId,
            "save flow: the game's callback runs once, on the game thread, with the complete file in place");
        check(IsCompleteSave(path, entities) && !File.Exists(path + SaveJob.TempSuffix) && flow.Snapshots == 1 &&
              flow.Thumbnail.Calls == 1 && flow.Optional.Calls == 0,
            "save flow: one snapshot, every entry written once, optional entries that decline are left out");

        // A second save while the first is still being written waits for it; both files are complete.
        callbacks = 0;
        string first = flow.Save("first", "first", () => callbacks++, true);
        string second = flow.Save("second", "second", () => callbacks++, true);
        check(IsCompleteSave(first, entities), "save flow: a save that starts while another is being written waits for it first");
        WaitUntilIdle();
        check(callbacks == 2 && IsCompleteSave(second, entities), "save flow: both saves complete and both callbacks run");

        // Anything that asks the repository about files finds the save complete.
        string asked = flow.Save("asked", "asked", null, true);
        BackgroundSave.WaitPrefix();
        check(IsCompleteSave(asked, entities), "save flow: asking the repository about saves waits for the one being written");
        WaitUntilIdle();

        // Instant saves (save on exit, BeaverBuddies' rehost) are the game's own, complete on return.
        int snapshots = flow.Snapshots;
        callbacks = 0;
        string instant = flow.Save("exit", "exit", () => callbacks++, false, instant: true);
        check(callbacks == 1 && flow.Snapshots == snapshots && flow.GameWrites == 1 && BackgroundSave.RunningCount == 0 &&
              IsCompleteSave(instant, entities),
            "save flow: an instant save is left to the game and its file is there when it returns");

        // An instant save that arrives while a queued one is pending, outside SaveInstantly (BeaverBuddies moves
        // saves to a tick boundary): prepared here, but written into the game's own stream, complete on return.
        Action pendingCallback = () => callbacks++;
        BackgroundSave.QueuePrefix("pending", ref pendingCallback);
        callbacks = 0;
        string moved = flow.Save("moved", "some other save", () => callbacks++, false);
        check(callbacks == 1 && IsCompleteSave(moved, entities) && BackgroundSave.RunningCount == 0,
            "save flow: a save that is not the queued one is complete when GameSaver.Save returns");
        string pending = flow.Save("pending", "pending", pendingCallback, false);
        WaitUntilIdle();
        check(callbacks == 2 && IsCompleteSave(pending, entities), "save flow: and the queued one still goes through afterwards");

        // Another mod supplies the file service: the prepared save is written into whatever stream the game got.
        callbacks = 0;
        string foreign = flow.Save("foreign", "foreign", () => callbacks++, true, fileService: false);
        check(callbacks == 1 && IsCompleteSave(foreign, entities) && BackgroundSave.RunningCount == 0,
            "save flow: without the game's FileService the save is written into the game's stream, never left empty");

        // The .saving file cannot be opened (its name is taken by a directory): the game's way, complete on return.
        Directory.CreateDirectory(Path.Combine(directory, "blocked.timber" + SaveJob.TempSuffix));
        callbacks = 0;
        string blocked = flow.Save("blocked", "blocked", () => callbacks++, true);
        check(callbacks == 1 && IsCompleteSave(blocked, entities) && BackgroundSave.IsActive,
            "save flow: if the .saving file cannot be opened the save is written the game's way");

        // A caller that is not GameSaver.Save gets the game's writer, whatever is pending.
        flow.Caller = "SomeMod.StateHasher::Hash";
        Action ignored = null;
        BackgroundSave.QueuePrefix("hash", ref ignored);
        using (MemoryStream memory = new MemoryStream())
        {
            check(BackgroundSave.WritePrefix(flow.Writer, memory, true), "save flow: another caller of the writer gets the game's own writer");
        }
        flow.Caller = "Timberborn.GameSaveRuntimeSystem.GameSaver::Save";
        using (MemoryStream memory = new MemoryStream())
        {
            check(BackgroundSave.WritePrefix(flow.Writer, memory, false), "save flow: a save to a stream (leaveOpen false) gets the game's own writer");
        }
        BackgroundSave.EveryFramePrefix();

        // The worker fails (the target is a directory, so the rename can never work): saved again on the game
        // thread, which fails too; the callback must not run and the game gets its GameSaverException.
        Directory.CreateDirectory(Path.Combine(directory, "doomed.timber"));
        callbacks = 0;
        flow.Save("doomed", "doomed", () => callbacks++, true);
        Exception thrown = null;
        try
        {
            WaitUntilIdle();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }
        check(thrown != null && thrown.GetType().Name == "GameSaverException" && callbacks == 0 && !BackgroundSave.IsActive,
            "save flow: a save that cannot be written raises the game's GameSaverException and never reports completion");
        check(!File.Exists(Path.Combine(directory, "doomed.timber" + SaveJob.TempSuffix)), "save flow: and leaves no .saving file behind");

        // Switched off, everything is the game's.
        callbacks = 0;
        string off = flow.Save("off", "off", () => callbacks++, true);
        check(callbacks == 1 && BackgroundSave.RunningCount == 0 && IsCompleteSave(off, entities), "save flow: once off, saves are the game's own");

        // An entry writer that throws while preparing: off, and the game's writer runs the whole save.
        flow = NewFlow(directory, world);
        flow.Thumbnail.Throw = true;
        Action none = null;
        BackgroundSave.QueuePrefix("broken", ref none);
        using (MemoryStream memory = new MemoryStream())
        {
            check(BackgroundSave.WritePrefix(flow.Writer, memory, true) && !BackgroundSave.IsActive && memory.Length == 0,
                "save flow: a failure while preparing hands the whole save to the game and turns the feature off");
        }

        // A new scene: the file is finished, the old scene's callback is dropped.
        flow = NewFlow(directory, world);
        callbacks = 0;
        string old = flow.Save("oldscene", "oldscene", () => callbacks++, true);
        BackgroundSave.SceneCreated();
        check(IsCompleteSave(old, entities) && callbacks == 0 && BackgroundSave.RunningCount == 0,
            "save flow: a new scene finds the save complete and does not run the old scene's callback");
    }

    // ------------------------------------------------------------------ district counts

    private sealed class ModDisallower : IGoodDisallower
    {
        public static int Thread;
        public static int CallsOnOtherThreads;
        public static int Calls;

#pragma warning disable CS0067
        public event EventHandler<DisallowedGoodsChangedEventArgs> DisallowedGoodsChanged;
#pragma warning restore CS0067

        public int AllowedAmount(string goodId)
        {
            Interlocked.Increment(ref Calls);
            if (Environment.CurrentManagedThreadId != Thread)
            {
                Interlocked.Increment(ref CallsOnOtherThreads);
            }
            return goodId.Length * 7;
        }
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo info = null;
        for (Type type = target.GetType(); type != null && info == null; type = type.BaseType)
        {
            info = type.GetField(field, Any | BindingFlags.DeclaredOnly);
        }
        if (info == null)
        {
            throw new MissingFieldException(target.GetType().Name, field);
        }
        info.SetValue(target, value);
    }

    private static Inventory NewInventory(Random random, string[] goods, int index)
    {
        Inventory inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        StorableGoodRegistry allowed = new StorableGoodRegistry();
        List<StorableGoodAmount> amounts = new List<StorableGoodAmount>();
        GoodRegistry storage = new GoodRegistry();
        int allowedCount = index % 5 == 0 ? goods.Length : 1 + random.Next(4);
        int start = random.Next(goods.Length);
        for (int i = 0; i < allowedCount; i++)
        {
            string good = goods[(start + i) % goods.Length];
            StorableGood storable = random.Next(3) == 0 ? StorableGood.CreateAsGivable(good)
                : random.Next(2) == 0 ? StorableGood.CreateAsTakeable(good) : StorableGood.CreateGiveableAndTakeable(good);
            amounts.Add(new StorableGoodAmount(storable, 10 + random.Next(500)));
            if (random.Next(2) == 0)
            {
                storage.Add(new GoodAmount(good, 1 + random.Next(60)));
            }
        }
        if (index % 7 == 3)
        {
            // A second entry for a good already allowed, with another amount: StorableGoodRegistry.GetAmount answers
            // the first entry's amount for both.
            amounts.Add(new StorableGoodAmount(StorableGood.CreateAsGivable(goods[start]), 3 + random.Next(50)));
        }
        allowed.Add(amounts);
        Set(inventory, "_allowedGoods", allowed);
        Set(inventory, "_storage", storage);
        Set(inventory, "<PublicInput>k__BackingField", random.Next(2) == 0);
        Set(inventory, "<PublicOutput>k__BackingField", true);
        Set(inventory, "_ignorableCapacity", index % 11 == 0);

        object disallower;
        switch (index % 4)
        {
            case 0:
                disallower = Activator.CreateInstance(typeof(Inventory).Assembly.GetType("Timberborn.InventorySystem.NullGoodDisallower", true), true);
                break;
            case 1:
                disallower = RuntimeHelpers.GetUninitializedObject(typeof(SingleGoodAllower));
                Set(disallower, "_inventory", inventory);
                Set(disallower, "<AllowedGood>k__BackingField", goods[start]);
                break;
            case 2:
                Type recipe = Assembly.Load("Timberborn.Workshops").GetType("Timberborn.Workshops.RecipeGoodDisallower", true);
                disallower = RuntimeHelpers.GetUninitializedObject(recipe);
                Dictionary<string, int> limits = new Dictionary<string, int>();
                for (int i = 0; i < allowedCount; i += 2)
                {
                    limits[goods[(start + i) % goods.Length]] = random.Next(40);
                }
                Set(disallower, "_limits", limits);
                break;
            default:
                disallower = new ModDisallower();
                break;
        }
        Set(inventory, "_goodDisallower", disallower);
        return inventory;
    }

    private static DistrictResourceCounter NewCounter(HashSet<Inventory> inventories)
    {
        Type counterType = typeof(DistrictResourceCounter);
        DistrictResourceCounter counter = (DistrictResourceCounter)RuntimeHelpers.GetUninitializedObject(counterType);
        foreach (string field in new[] { "_stockCounter", "_capacityCounter", "_processedGoodCounter", "_availableCarriedGoods",
                     "_reservedCarriedGoods", "_goodCarriers" })
        {
            Set(counter, field, Activator.CreateInstance(counterType.GetField(field, Any).FieldType, true));
        }
        DistrictInventoryRegistry registry = (DistrictInventoryRegistry)RuntimeHelpers.GetUninitializedObject(typeof(DistrictInventoryRegistry));
        Type inner = typeof(DistrictInventoryRegistry).GetField("_publicInventoryRegistry", Any).FieldType;
        object innerRegistry = RuntimeHelpers.GetUninitializedObject(inner);
        Set(innerRegistry, "_inventories", inventories);
        Set(registry, "_publicInventoryRegistry", innerRegistry);
        Set(counter, "_districtInventoryRegistry", registry);
        return counter;
    }

    private static string Counts(DistrictResourceCounter counter, string[] goods)
    {
        StringBuilder text = new StringBuilder();
        foreach (string good in goods.Concat(new[] { "NotAGood" }))
        {
            ResourceCount count = counter.GetResourceCount(good);
            text.Append(good).Append(':').Append(count.StockpiledStock).Append(',').Append(count.BufferedOutputStock).Append(',')
                .Append(count.InputOutputCapacity).Append(',').Append(count.TotalCapacity).Append(',').Append(count.AvailableStock)
                .Append(',').Append(count.AllStock).Append(';');
        }
        return text.ToString();
    }

    private static void RunDistrictCounts(Action<bool, string> check)
    {
        DistrictCounts.ResetForTests();
        DistrictCounts.CreateFeature(new Config { DistrictCountsVerify = true });
        DistrictCounts.BindAccessors();
        DistrictCounts.PatchesOn = method => new[] { (Plugin.HarmonyId + ".HaulCache", "LateGamePerformance.HaulCache.InputChangedPostfix") };
        DistrictCounts.Activate();
        ModDisallower.Thread = Environment.CurrentManagedThreadId;
        ModDisallower.Calls = ModDisallower.CallsOnOtherThreads = 0;

        string[] goods = Enumerable.Range(0, 30).Select(i => "Good" + i).ToArray();
        Random random = new Random(11);
        HashSet<Inventory> inventories = new HashSet<Inventory>();
        List<Inventory> list = new List<Inventory>();
        for (int i = 0; i < 700; i++)
        {
            Inventory inventory = NewInventory(random, goods, i);
            inventories.Add(inventory);
            list.Add(inventory);
        }
        DistrictResourceCounter games = NewCounter(inventories);
        DistrictResourceCounter mods = NewCounter(inventories);

        bool same = true, replaced = true, sameTables = true;
        string last = null;
        int different = 0;
        for (int round = 0; round < 6; round++)
        {
            // Stock moves between rounds, as it does between ticks.
            foreach (Inventory inventory in list)
            {
                if (random.Next(3) == 0 && inventory.AllowedGoods.Count > 0)
                {
                    GoodRegistry storage = (GoodRegistry)typeof(Inventory).GetField("_storage", Any).GetValue(inventory);
                    storage.Add(new GoodAmount(inventory.AllowedGoods[random.Next(inventory.AllowedGoods.Count)].StorableGood.GoodId, 1 + random.Next(9)));
                }
            }
            games.UpdateCounters();
            replaced &= !(round % 2 == 0 ? DistrictCounts.UpdatePrefix(mods) : DistrictCounts.TickPrefix(mods));
            string expected = Counts(games, goods);
            same &= expected == Counts(mods, goods);
            sameTables &= Tables(games) == Tables(mods);
            if (expected != last)
            {
                different++;
            }
            last = expected;
        }
        check(replaced && same && different == 6,
            "district counts: 700 real inventories, six rounds, every good reads the same as after the game's own count");
        check(sameTables,
            "district counts: the four tables hold the game's keys, values and insertion order exactly, not only the same reads");
        check(ModDisallower.Calls > 0 && ModDisallower.CallsOnOtherThreads == 0,
            $"district counts: every capacity rule, the game's and a mod's, is asked on the game thread ({ModDisallower.Calls} calls)");
        string line = DistrictCounts.TakeStatsLine();
        check(line != null && line.Contains("6 counts of 700 inventories on the main thread") &&
              line.Contains("allowed goods read anew for 700 inventories") && line.Contains("verify mismatches 0") &&
              line.Contains("the game's own count of the same took"), "district counts: stats line (" + line + ")");

        // Re-initialised or replaced: the kept allowed goods are read again, and the counts stay the game's.
        Inventory changed = list.First(inventory => inventory.AllowedGoods.Count > 0 &&
                                                    !(bool)typeof(Inventory).GetField("_ignorableCapacity", Any).GetValue(inventory));
        StorableGoodRegistry registry = (StorableGoodRegistry)typeof(Inventory).GetField("_allowedGoods", Any).GetValue(changed);
        registry.Add(new List<StorableGoodAmount>
        {
            new StorableGoodAmount(StorableGood.CreateGiveableAndTakeable("Good29"), 77),
            new StorableGoodAmount(StorableGood.CreateGiveableAndTakeable(changed.AllowedGoods[0].StorableGood.GoodId), 5)
        });
        games.UpdateCounters();
        DistrictCounts.UpdatePrefix(mods);
        bool afterAdd = Counts(games, goods) == Counts(mods, goods);
        string addLine = DistrictCounts.TakeStatsLine();
        Inventory other = list.Last(inventory => inventory.AllowedGoods.Count > 2);
        StorableGoodRegistry swapped = new StorableGoodRegistry();
        swapped.Add(other.AllowedGoods.Reverse().Select(good => new StorableGoodAmount(good.StorableGood, good.Amount + 3)).ToList());
        Set(other, "_allowedGoods", swapped);
        Inventory replacement = NewInventory(new Random(99), goods, 5);
        inventories.Remove(list[7]);
        inventories.Add(replacement);
        games.UpdateCounters();
        DistrictCounts.UpdatePrefix(mods);
        bool afterSwap = Counts(games, goods) == Counts(mods, goods);
        string swapLine = DistrictCounts.TakeStatsLine();
        inventories.Remove(replacement);
        inventories.Add(list[7]);
        check(afterAdd && addLine.Contains("allowed goods read anew for 1 inventories") && afterSwap &&
              swapLine.Contains("allowed goods read anew for 2 inventories"),
            "district counts: an inventory initialised again (more allowed goods, one of them a second entry for a good " +
            "it had), a replaced allowed-goods registry of the same size and a replaced inventory are read again, and " +
            "every good still reads the game's count");

        // What is read on every count, never kept: ignorable capacity (the emptying registry switches it) and the
        // capacity rule itself.
        Inventory toggled = list.First(inventory => !(bool)typeof(Inventory).GetField("_ignorableCapacity", Any).GetValue(inventory) &&
                                                    inventory.AllowedGoods.Count > 3);
        toggled.SetIgnorableCapacity(true);
        Inventory ruled = list.First(inventory => inventory != toggled && inventory.AllowedGoods.Count > 3 &&
                                                  !(bool)typeof(Inventory).GetField("_ignorableCapacity", Any).GetValue(inventory));
        object previousRule = typeof(Inventory).GetField("_goodDisallower", Any).GetValue(ruled);
        Set(ruled, "_goodDisallower", new ModDisallower());
        games.UpdateCounters();
        DistrictCounts.UpdatePrefix(mods);
        bool live = Counts(games, goods) == Counts(mods, goods);
        toggled.SetIgnorableCapacity(false);
        Set(ruled, "_goodDisallower", previousRule);
        games.UpdateCounters();
        DistrictCounts.UpdatePrefix(mods);
        check(live && Counts(games, goods) == Counts(mods, goods),
            "district counts: ignorable capacity and the capacity rule are read on every count, so switching either reads the game's count");
        DistrictCounts.TakeStatsLine();

        // The count at the logged colony's size, for the record: the game's loop, the walk on the main thread, and the
        // walk dealt out to the worker threads.
        RunDistrictCountsTiming(goods, games, mods);

        // Every district is counted by the mod now, however small: the walk has no threads to wake.
        HashSet<Inventory> few = new HashSet<Inventory>(list.Take(40));
        DistrictResourceCounter smallGames = NewCounter(few);
        DistrictResourceCounter small = NewCounter(few);
        smallGames.UpdateCounters();
        check(!DistrictCounts.UpdatePrefix(small) && Counts(smallGames, goods) == Counts(small, goods),
            "district counts: a small district is counted by the mod too, with the game's numbers");

        check(DistrictCounts.Difference(new Dictionary<string, int> { { "a", 1 }, { "b", 0 } }, new Dictionary<string, int> { { "a", 1 } }) == null &&
              DistrictCounts.Difference(new Dictionary<string, int> { { "a", 1 } }, new Dictionary<string, int> { { "a", 2 } }) != null &&
              DistrictCounts.Difference(new Dictionary<string, int> { { "a", 1 }, { "c", 3 } }, new Dictionary<string, int> { { "a", 1 } }) != null,
            "district counts: the comparison reads both tables the way the game does, value or 0");

        // Another mod patches something the walk stands in for: the game's own count runs, quietly.
        foreach ((string type, string method) in new[]
                 {
                     ("Inventory", "GetCapacity"), ("Inventory", "LimitedAmount"), ("Inventory", "Gives"), ("Inventory", "get_AllowedGoods"),
                     ("StorableGoodRegistry", "GetAmount"), ("CapacityCounter", "CountInventoryCapacity"), ("StockCounter", "UpdateStock")
                 })
        {
            DistrictCounts.ResetForTests();
            DistrictCounts.PatchesOn = target => target.Name == method && target.DeclaringType.Name == type
                ? new[] { ("some.other.mod", "Other.Patch.Prefix") } : null;
            DistrictCounts.Activate();
            check(DistrictCounts.UpdatePrefix(mods) && !DistrictCounts.IsActive,
                $"district counts: if another mod patches {type}.{method}, the game's own count runs");
        }

        // A patch on a capacity rule runs for the mod as for the game (the rules are called on the game thread, the
        // way the game calls them), so MixedStorage's allocation limit needs no review any more.
        List<string> accepted = new List<string>();
        DistrictCounts.PatchesOn = method => method.Name == "AllowedAmount" && method.DeclaringType.Name == "SingleGoodAllower"
            ? new[] { ("kyler.mixedstorage", "MixedStorage.LimitPatch.Prefix") } : null;
        check(DistrictCounts.ForeignPatch(accepted) == null && accepted.Count == 0 && DistrictCounts.ReviewedPatches.Length == 0,
            "district counts: a patch on a capacity rule (MixedStorage's allocation limit) leaves the count to the mod");

        DistrictCounts.PatchesOn = method => null;
        RunDistrictCountsVerifyOnlyMeasures(check, list, goods);

        DistrictCounts.ResetForTests();
        DistrictCounts.CreateFeature(new Config());
        DistrictCounts.Activate();

        // A broken inventory: the feature turns itself off and the game's count runs (and fails its own way).
        Inventory broken = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        Set(broken, "_goodDisallower", Activator.CreateInstance(typeof(Inventory).Assembly.GetType("Timberborn.InventorySystem.NullGoodDisallower", true), true));
        inventories.Add(broken);
        check(DistrictCounts.UpdatePrefix(mods) && !DistrictCounts.IsActive, "district counts: a failure turns the feature off and hands the count to the game");
        inventories.Remove(broken);
        games.UpdateCounters();
        mods.UpdateCounters();
        check(Counts(games, goods) == Counts(mods, goods), "district counts: and the game's count then leaves nothing of the failed one behind");
    }

    // The four tables as the game holds them: keys, values and order.
    private static string Tables(DistrictResourceCounter counter)
    {
        object stock = typeof(DistrictResourceCounter).GetField("_stockCounter", Any).GetValue(counter);
        object capacity = typeof(DistrictResourceCounter).GetField("_capacityCounter", Any).GetValue(counter);
        StringBuilder text = new StringBuilder();
        foreach ((object owner, string field) in new[]
                 {
                     (stock, "_outputStock"), (stock, "_inputOutputStock"), (capacity, "_outputCapacity"), (capacity, "_inputOutputCapacity")
                 })
        {
            foreach (KeyValuePair<string, int> pair in (Dictionary<string, int>)owner.GetType().GetField(field, Any).GetValue(owner))
            {
                text.Append(pair.Key).Append('=').Append(pair.Value).Append(',');
            }
            text.Append('|');
        }
        return text.ToString();
    }

    // MixedStorage's allocation rule under Mono: a ConditionalWeakTable lookup, which takes the table's one lock, on
    // every AllowedAmount call. Here a lookup under a lock shared by every storage.
    private sealed class LockedRule : IGoodDisallower
    {
        private static readonly object Gate = new object();
        public Dictionary<string, int> Limits;

#pragma warning disable CS0067
        public event EventHandler<DisallowedGoodsChangedEventArgs> DisallowedGoodsChanged;
#pragma warning restore CS0067

        public int AllowedAmount(string goodId)
        {
            lock (Gate)
            {
                return Limits.TryGetValue(goodId, out int limit) ? limit : 0;
            }
        }
    }

    // A district like the logged one: 236 inventories, 38 of them warehouses allowing 25 goods each, the rest workshops,
    // flags and piles with one to four. Timed the game's way, the mod's way on the main thread, and the mod's walk dealt
    // out to the worker threads (TickWorkers, as up to 0.4.27, with a table per worker added up on the main thread),
    // each with the warehouses under the game's single-good rule and under a locked rule (MixedStorage under Mono).
    private static void RunDistrictCountsTiming(string[] goods, DistrictResourceCounter games, DistrictResourceCounter mods)
    {
        foreach (bool locked in new[] { false, true })
        {
            Random random = new Random(21);
            HashSet<Inventory> district = new HashSet<Inventory>();
            for (int i = 0; i < 236; i++)
            {
                Inventory inventory;
                if (i < 38)
                {
                    inventory = NewInventory(random, goods, 1);
                    StorableGoodRegistry allowed = new StorableGoodRegistry();
                    allowed.Add(goods.Take(25).Select(good => new StorableGoodAmount(StorableGood.CreateGiveableAndTakeable(good), 180)).ToList());
                    Set(inventory, "_allowedGoods", allowed);
                    Set(inventory, "_ignorableCapacity", false);
                    if (locked)
                    {
                        Set(inventory, "_goodDisallower", new LockedRule { Limits = goods.Take(25).ToDictionary(good => good, good => 7) });
                    }
                }
                else
                {
                    inventory = NewInventory(random, goods, i % 5 == 0 ? i + 1 : i);
                    if (typeof(Inventory).GetField("_goodDisallower", Any).GetValue(inventory) is ModDisallower)
                    {
                        Set(inventory, "_goodDisallower", Activator.CreateInstance(
                            typeof(Inventory).Assembly.GetType("Timberborn.InventorySystem.NullGoodDisallower", true), true));
                    }
                }
                district.Add(inventory);
            }
            DistrictResourceCounter gameCounter = NewCounter(district);
            DistrictResourceCounter modCounter = NewCounter(district);
            DistrictCounts.ResetForTests();
            DistrictCounts.CreateFeature(new Config());
            DistrictCounts.Activate();
            DistrictCounts.UpdatePrefix(modCounter);
            gameCounter.UpdateCounters();
            bool agrees = Counts(gameCounter, goods) == Counts(modCounter, goods);

            Inventory[] items = district.ToArray();
            int workers = Math.Max(1, TickWorkers.Maximum);
            Dictionary<string, int>[][] tallies = Enumerable.Range(0, workers)
                .Select(_ => Enumerable.Range(0, 4).Select(__ => new Dictionary<string, int>()).ToArray()).ToArray();
            Dictionary<string, int>[] merged = Enumerable.Range(0, 4).Select(_ => new Dictionary<string, int>()).ToArray();
            DistrictCounts.AllowedGoods[] lists = new DistrictCounts.AllowedGoods[items.Length];
            void OnWorkers()
            {
                for (int i = 0; i < items.Length; i++)
                {
                    lists[i] = DistrictCounts.AllowedOf(items[i]);
                }
                int used = 1;
                TickWorkers.Run(workers, (worker, sharing) =>
                {
                    if (worker == 0)
                    {
                        used = sharing;
                    }
                    Dictionary<string, int>[] tally = tallies[worker];
                    foreach (Dictionary<string, int> table in tally)
                    {
                        table.Clear();
                    }
                    for (int i = worker; i < items.Length; i += sharing)
                    {
                        DistrictCounts.CountStock(items[i], tally[0], tally[1]);
                        DistrictCounts.CountCapacity(items[i], lists[i], tally[2], tally[3]);
                    }
                });
                for (int table = 0; table < 4; table++)
                {
                    merged[table].Clear();
                    for (int worker = 0; worker < used; worker++)
                    {
                        foreach (KeyValuePair<string, int> pair in tallies[worker][table])
                        {
                            DistrictCounts.Add(merged[table], pair.Key, pair.Value);
                        }
                    }
                }
            }
            double Time(Action count)
            {
                for (int i = 0; i < 200; i++)
                {
                    count();
                }
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                {
                    count();
                }
                return watch.Elapsed.TotalMilliseconds / 2000;
            }
            double gameMs = Time(() => gameCounter.UpdateCounters());
            double mainMs = Time(() => DistrictCounts.UpdatePrefix(modCounter));
            double workersMs = Time(OnWorkers);
            // One count per tick, with the rest of the tick in between (the workers go back to sleep).
            double sleptMs = 0;
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(2);
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                OnWorkers();
                sleptMs += watch.Elapsed.TotalMilliseconds / 100;
            }
            Console.WriteLine($"     timing{(agrees ? "" : " (COUNTS DIFFER)")}: one count of 236 inventories (38 warehouses x 25 goods), " +
                              (locked ? "warehouses under a locked rule (MixedStorage under Mono)" : "warehouses under the game's single-good rule") +
                              $": the game {gameMs * 1000:0} us, the walk on the main thread {mainMs * 1000:0} us, the walk on {workers} " +
                              $"worker threads {workersMs * 1000:0} us back to back and {sleptMs * 1000:0} us with 2 ms between counts");
        }
        DistrictCounts.ResetForTests();
        DistrictCounts.CreateFeature(new Config());
        DistrictCounts.Activate();
        DistrictCounts.TakeStatsLine();
    }

    // A capacity rule that answers differently every time it is asked: the game's count in verify mode then comes out
    // different from the mod's, as it would if the mod had a counting bug.
    private sealed class DriftingDisallower : IGoodDisallower
    {
        public static int Calls;

#pragma warning disable CS0067
        public event EventHandler<DisallowedGoodsChangedEventArgs> DisallowedGoodsChanged;
#pragma warning restore CS0067

        public int AllowedAmount(string goodId)
        {
            return 10 + Calls++;
        }
    }

    // A verify key is each player's own, so what the game reads must not depend on it: the same district is counted
    // with DistrictCountsVerify off and on, with the game's count made to differ, and every good must read the same.
    private static void RunDistrictCountsVerifyOnlyMeasures(Action<bool, string> check, List<Inventory> list, string[] goods)
    {
        HashSet<Inventory> district = new HashSet<Inventory>(list.Take(150));
        Inventory drifting = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        StorableGoodRegistry allowed = new StorableGoodRegistry();
        allowed.Add(new List<StorableGoodAmount> { new StorableGoodAmount(StorableGood.CreateAsTakeable(goods[0]), 500) });
        Set(drifting, "_allowedGoods", allowed);
        Set(drifting, "_storage", new GoodRegistry());
        Set(drifting, "<PublicInput>k__BackingField", false);
        Set(drifting, "<PublicOutput>k__BackingField", true);
        Set(drifting, "_ignorableCapacity", false);
        Set(drifting, "_goodDisallower", new DriftingDisallower());
        district.Add(drifting);

        string CountWith(bool verify, out string line)
        {
            DistrictCounts.ResetForTests();
            DistrictCounts.CreateFeature(new Config { DistrictCountsVerify = verify });
            DistrictCounts.Activate();
            DriftingDisallower.Calls = 0;
            DistrictResourceCounter counter = NewCounter(district);
            bool replaced = !DistrictCounts.UpdatePrefix(counter);
            line = DistrictCounts.TakeStatsLine();
            return replaced ? Counts(counter, goods) : null;
        }

        string off = CountWith(false, out _);
        string on = CountWith(true, out string verifyLine);
        check(off != null && off == on && DriftingDisallower.Calls == 2 && verifyLine.Contains("verify mismatches 1"),
            "district counts verify: the game's count came out different and was logged, and every good reads the mod's count, " +
            $"as with verify off ({verifyLine})");
    }
}
