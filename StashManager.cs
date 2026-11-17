using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MoonUI
{
    public enum StashItemType
    {
        Text,
        File
    }

    public class StashItem
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public StashItemType Type { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
    }

    public static class StashManager
    {
        private static readonly string StashFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MoonIsland");

        private static readonly string StashFilePath = Path.Combine(StashFolder, "stash.json");

        public static List<StashItem> Items { get; private set; } = Load();

        public static event Action? StashUpdated;

        private static List<StashItem> Load()
        {
            try
            {
                if (File.Exists(StashFilePath))
                {
                    string json = File.ReadAllText(StashFilePath);
                    var loaded = JsonSerializer.Deserialize<List<StashItem>>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Stash konnte nicht geladen werden: {ex.Message}");
            }

            return new List<StashItem>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(StashFolder);
                string json = JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StashFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Stash konnte nicht gespeichert werden: {ex.Message}");
            }
        }

        public static void AddText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string trimmed = text.Trim();

            if (Items.Count > 0 && Items[0].Type == StashItemType.Text && Items[0].Content == trimmed)
                return;

            string preview = trimmed.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 55) preview = preview.Substring(0, 55) + "...";

            Items.Insert(0, new StashItem
            {
                Type = StashItemType.Text,
                Title = preview,
                Content = trimmed
            });

            TrimAndNotify();
        }

        public static void AddFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
                return;

            if (Items.Count > 0 && Items[0].Type == StashItemType.File &&
                Items[0].Content.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                return;

            Items.Insert(0, new StashItem
            {
                Type = StashItemType.File,
                Title = Path.GetFileName(filePath),
                Content = filePath
            });

            TrimAndNotify();
        }

        public static void RemoveItem(StashItem item)
        {
            Items.RemoveAll(x => x.Id == item.Id);
            Save();
            StashUpdated?.Invoke();
        }

        public static void ClearAll()
        {
            Items.Clear();
            Save();
            StashUpdated?.Invoke();
        }

        private static void TrimAndNotify()
        {
            if (Items.Count > 30)
            {
                Items = Items.Take(30).ToList();
            }

            Save();
            StashUpdated?.Invoke();
        }
    }
}