// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using Newtonsoft.Json;

namespace network.common.data
{
    /// <summary>
    ///     #185 - Battle Royale style item recipes.
    ///     This is separate from PartRecipeData because it combines item IDs directly,
    ///     not mission parts.
    /// </summary>
    public static class BattleItemRecipeData
    {
        private static readonly Dictionary<int, BattleItemRecipe> _recipesById = new();
        private static readonly Dictionary<int, List<BattleItemRecipe>> _recipesByOutput = new();

        public static void Initialize(List<CsvRow> data)
        {
            _recipesById.Clear();
            _recipesByOutput.Clear();

            foreach (var row in data)
            {
                var recipe = BattleItemRecipe.CreateFromData(row);
                _recipesById[recipe.RecipeId] = recipe;

                if (!_recipesByOutput.TryGetValue(recipe.OutputItemId, out var recipes))
                {
                    recipes = new List<BattleItemRecipe>();
                    _recipesByOutput[recipe.OutputItemId] = recipes;
                }

                recipes.Add(recipe);
            }
        }

        public static BattleItemRecipe Get(int recipeId) =>
            _recipesById.GetValueOrDefault(recipeId);

        public static List<BattleItemRecipe> GetAllRecipes() =>
            _recipesById.Values.OrderBy(recipe => recipe.RecipeId).ToList();

        public static List<BattleItemRecipe> GetRecipesByOutput(int outputItemId) =>
            _recipesByOutput.TryGetValue(outputItemId, out var recipes)
                ? recipes.OrderBy(recipe => recipe.RecipeId).ToList()
                : new List<BattleItemRecipe>();

        public static BattleItemRecipe TryCombine(IEnumerable<int> inputItemIds)
        {
            var normalizedInputs = NormalizeInputs(inputItemIds);
            if (normalizedInputs.Count == 0) return null;

            return _recipesById.Values.FirstOrDefault(recipe =>
                NormalizeInputs(recipe.InputItemIds).SequenceEqual(normalizedInputs));
        }

        public static void ValidateReferentialIntegrity(List<string> errors, HashSet<int> itemIds)
        {
            var seenRecipeIds = new HashSet<int>();

            foreach (var recipe in GetAllRecipes())
            {
                if (!seenRecipeIds.Add(recipe.RecipeId))
                    errors.Add($"battle_item_recipe [{recipe.RecipeId}]: duplicate recipe_id");

                if (!itemIds.Contains(recipe.OutputItemId))
                    errors.Add($"battle_item_recipe [{recipe.RecipeId}]: output_item_id={recipe.OutputItemId} not found in item_info");

                if (recipe.InputItemIds.Count < 2)
                    errors.Add($"battle_item_recipe [{recipe.RecipeId}]: input_item_ids must contain at least 2 items");

                foreach (var inputItemId in recipe.InputItemIds)
                {
                    if (!itemIds.Contains(inputItemId))
                        errors.Add($"battle_item_recipe [{recipe.RecipeId}]: input_item_id={inputItemId} not found in item_info");
                }

                if (recipe.CraftSeconds <= 0)
                    errors.Add($"battle_item_recipe [{recipe.RecipeId}]: craft_seconds must be positive");

                if (string.IsNullOrWhiteSpace(recipe.Category))
                    errors.Add($"battle_item_recipe [{recipe.RecipeId}]: category is required");
            }
        }

        private static List<int> NormalizeInputs(IEnumerable<int> inputItemIds) =>
            inputItemIds?
                .Where(id => id > 0)
                .OrderBy(id => id)
                .ToList()
            ?? new List<int>();
    }

    public class BattleItemRecipe
    {
        public int RecipeId { get; private set; }
        public int OutputItemId { get; private set; }
        public List<int> InputItemIds { get; private set; }
        public int CraftSeconds { get; private set; }
        public string Category { get; private set; }
        public string RouteType { get; private set; }

        public static BattleItemRecipe CreateFromData(CsvRow row)
        {
            var inputItemIds = JsonConvert.DeserializeObject<List<int>>(row["input_item_ids"]) ?? new List<int>();

            return new BattleItemRecipe
            {
                RecipeId = int.Parse(row["recipe_id"]),
                OutputItemId = int.Parse(row["output_item_id"]),
                InputItemIds = inputItemIds,
                CraftSeconds = int.Parse(row["craft_seconds"]),
                Category = row["category"]?.Trim() ?? "",
                RouteType = row["route_type"]?.Trim() ?? ""
            };
        }
    }
}
