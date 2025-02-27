﻿#pragma warning disable 1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sanakan.Api.Models;
using Sanakan.Config;
using Sanakan.Database.Models;
using Sanakan.Extensions;
using Sanakan.Services.Executor;
using Sanakan.Services.PocketWaifu;
using Sanakan.Services.Time;
using Shinden;
using Z.EntityFramework.Plus;

namespace Sanakan.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WaifuController : ControllerBase
    {
        private readonly Waifu _waifu;
        private readonly IConfig _config;
        private readonly ISystemTime _time;
        private readonly IExecutor _executor;
        private readonly ShindenClient _shClient;
        private readonly IMemoryCache _nameCache;
        private readonly DiscordSocketClient _client;

        public WaifuController(ShindenClient shClient, Waifu waifu, IExecutor executor, IConfig config, ISystemTime time, IMemoryCache cache, DiscordSocketClient client)
        {
            _time = time;
            _waifu = waifu;
            _config = config;
            _client = client;
            _nameCache = cache;
            _executor = executor;
            _shClient = shClient;
        }

        /// <summary>
        /// Pobiera użytkowników posiadających karte postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <returns>lista id</returns>
        /// <response code="404">Users not found</response>
        [HttpGet("users/owning/character/{id}"), Authorize(Policy = "Site")]
        public async Task<ActionResult<IEnumerable<ulong>>> GetUsersOwningCharacterCardAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var shindenIds = await db.Cards.Include(x => x.GameDeck).ThenInclude(x => x.User)
                    .Where(x => x.Character == id && x.GameDeck.User.Shinden != 0).AsNoTracking().Select(x => x.GameDeck.User.Shinden).Distinct().ToListAsync();

                if (shindenIds.Count > 0)
                    return shindenIds;

                return "Users not found".ToResponse(404);
            }
        }

        /// <summary>
        /// Pobiera liste kart użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <returns>lista kart</returns>
        /// <response code="404">User not found</response>
        [HttpGet("user/{id}/cards"), Authorize(Policy = "Site")]
        public async Task<ActionResult<IEnumerable<Database.Models.Card>>> GetUserCardsAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.Users.AsQueryable().Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Cards).ThenInclude(x => x.ArenaStats).Include(x => x.GameDeck)
                    .ThenInclude(x => x.Cards).ThenInclude(x => x.TagList).AsNoTracking().AsSplitQuery().FirstOrDefaultAsync();

                if (user == null)
                {
                    return "User not found".ToResponse(404);
                }

                return user.GameDeck.Cards.ToList();
            }
        }

        /// <summary>
        /// Pobiera listę aktywności
        /// </summary>
        /// <param name="count">liczba wpisów</param>
        /// <param name="users">id użytkowników shinden</param>
        /// <returns>lista aktywności</returns>
        [HttpPost("user/activity/{count}")]
        public async Task<IEnumerable<UserActivity>> GetUsersActivitiesAsync(uint count, [FromBody]List<ulong> users)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var query = db.UserActivities.AsQueryable().AsSplitQuery().AsNoTracking();
                if (!users.IsNullOrEmpty())
                {
                    query = query.Where(x => users.Any(c => c == x.ShindenId));
                }
                return count == 0 ? await query.OrderByDescending(x => x.Id).ToListAsync() : await query.OrderByDescending(x => x.Id).Take((int)count).ToListAsync();
            }
        }

        /// <summary>
        /// Pobiera listę aktywności od konkretnego id
        /// </summary>
        /// <param name="lastId">id aktywności od której zacząć nową liste</param>
        /// <returns>lista aktywności</returns>
        [HttpGet("user/activity/{lastId}")]
        public async Task<IEnumerable<UserActivity>> GetUsersActivitiesFromIdAsync(ulong lastId)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                return await db.UserActivities.AsQueryable().Where(x => x.Id > lastId).AsNoTracking().OrderByDescending(x => x.Id).ToListAsync();
            }
        }

        /// <summary>
        /// Pobiera x kart z przefiltrowanej listy wszystkich kart
        /// </summary>
        /// <param name="offset">przesunięcie</param>
        /// <param name="count">liczba kart</param>
        /// <param name="filter">filtry listy</param>
        /// <returns>lista kart</returns>
        [HttpPost("user/total/cards/{offset}/{count}")]
        public async Task<FilteredCards> GetCardsWithOffsetAndFilterAsync(uint offset, uint count, [FromBody]CardsQueryFilter filter)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var query = db.Cards.AsQueryable().AsSplitQuery().Include(x=> x.ArenaStats).Include(x => x.TagList).AsNoTracking();
                if (!string.IsNullOrEmpty(filter.SearchText))
                {
                    query = query.Where(x => x.Name.Contains(filter.SearchText) || x.Title.Contains(filter.SearchText) || x.Id.ToString().Contains(filter.SearchText));
                }

                query = CardsQueryFilter.Use(filter.OrderBy, query);

                var cards = FilterCardsByTags((await query.FromCacheAsync("api-all-cards")).ToList(), filter);

                return new FilteredCards{TotalCards = cards.Count, Cards = cards.Skip((int)offset).Take((int)count).ToView()};
            }
        }

        /// <summary>
        /// Pobiera x kart z przefiltrowanej listy użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <param name="offset">przesunięcie</param>
        /// <param name="count">liczba kart</param>
        /// <param name="filter">filtry listy</param>
        /// <returns>lista kart</returns>
        /// <response code="404">User not found</response>
        [HttpPost("user/{id}/cards/{offset}/{count}")]
        public async Task<ActionResult<FilteredCards>> GetUsersCardsByShindenIdWithOffsetAndFilterAsync(ulong id, uint offset, uint count, [FromBody]CardsQueryFilter filter)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.Users.AsQueryable().Where(x => x.Shinden == id).Include(x => x.GameDeck).AsNoTracking().AsSplitQuery().FirstOrDefaultAsync();

                if (user == null)
                {
                    return "User not found".ToResponse(404);
                }

                if (user.IsBlacklisted)
                {
                    return "User on blacklist".ToResponse(401);
                }

                var query = db.Cards.AsQueryable().AsSplitQuery().Where(x => x.GameDeckId == user.GameDeck.Id).Include(x=> x.ArenaStats).Include(x => x.TagList).AsNoTracking();
                if (!string.IsNullOrEmpty(filter.SearchText))
                {
                    query = query.Where(x => x.Name.Contains(filter.SearchText) || x.Title.Contains(filter.SearchText) || x.Id.ToString().Contains(filter.SearchText));
                }

                query = CardsQueryFilter.Use(filter.OrderBy, query);

                var cards = FilterCardsByTags(await query.ToListAsync(), filter);

                return new FilteredCards{TotalCards = cards.Count, Cards = cards.Skip((int)offset).Take((int)count).ToView(id)};
            }
        }

        /// <summary>
        /// Pobiera x kart z listy użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <param name="offset">przesunięcie</param>
        /// <param name="count">liczba kart</param>
        /// <returns>lista kart</returns>
        /// <response code="404">User not found</response>
        [HttpGet("user/{id}/cards/{offset}/{count}")]
        public async Task<ActionResult<IEnumerable<CardFinalView>>> GetUsersCardsByShindenIdWithOffsetAsync(ulong id, uint offset, uint count)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.Users.AsQueryable().AsSplitQuery().Where(x => x.Shinden == id).Include(x => x.GameDeck).AsNoTracking().FirstOrDefaultAsync();

                if (user == null)
                {
                    return "User not found".ToResponse(404);
                }

                if (user.IsBlacklisted)
                {
                    return "User on blacklist".ToResponse(401);
                }

                var cards = await db.Cards.AsQueryable().AsSplitQuery().Where(x => x.GameDeckId == user.GameDeck.Id).Include(x=> x.ArenaStats).Include(x => x.TagList).Skip((int)offset).Take((int)count).AsNoTracking().ToListAsync();
                return cards.ToView(id);
            }
        }

        /// <summary>
        /// Pobiera kartę
        /// </summary>
        /// <param name="id">id karty</param>
        /// <returns>karta</returns>
        /// <response code="404">Card not found</response>
        [HttpGet("card/{id}/view")]
        public async Task<ActionResult<CardFinalView>> GetCardViewAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var card = await db.Cards.AsQueryable().Where(x => x.Id == id).Include(x => x.ArenaStats)
                    .Include(x => x.TagList).Include(x => x.GameDeck).ThenInclude(x => x.User)
                    .AsNoTracking().FirstOrDefaultAsync();

                if (card == null)
                {
                    return "Card not found".ToResponse(404);
                }

                if (card.GameDeck.User.Shinden != 0)
                {
                    if (_nameCache.TryGetValue(card.GameDeck.User.Shinden, out string username))
                    {
                        return card.ToViewUser(username);
                    }

                    if (card.GameDeck.User.Shinden == 1)
                    {
                        return card.ToViewUser(_client.CurrentUser.GetUserNickInGuild());
                    }

                    var res = await _shClient.User.GetAsync(card.GameDeck.User.Shinden);
                    if (res.IsSuccessStatusCode())
                    {
                        _nameCache.Set(card.GameDeck.User.Shinden, res.Body.Name, new MemoryCacheEntryOptions()
                            .SetAbsoluteExpiration(TimeSpan.FromHours(12)));

                        return card.ToViewUser(res.Body.Name);
                    }
                }
                return card.ToView();
            }
        }

        /// <summary>
        /// Pobiera surową listę życzeń użtykownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <returns>lista życzeń</returns>
        /// <response code="404">User not found</response>
        /// <response code="401">User wishlist is private</response>
        [HttpGet("user/shinden/{id}/wishlist/raw")]
        public async Task<ActionResult<IEnumerable<WishlistObject>>> GetUsersRawWishlistByShindenIdAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.Users.AsQueryable().AsSplitQuery().Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Wishes).AsNoTracking().FirstOrDefaultAsync();

                if (user == null)
                {
                    return "User not found".ToResponse(404);
                }

                if (user.IsBlacklisted)
                {
                    return "User on blacklist".ToResponse(401);
                }

                if (user.GameDeck.WishlistIsPrivate)
                {
                    return "User wishlist is private".ToResponse(401);
                }

                return user.GameDeck.Wishes.ToList();
            }
        }

        /// <summary>
        /// Pobiera topke życzeń użytkowników
        /// </summary>
        /// <param name="count">jak dużo wpisów</param>
        /// <returns>topka życzeń</returns>
        /// <response code="404">Not found</response>
        [HttpGet("top/characters/{count}")]
        public async Task<ActionResult<IEnumerable<Database.Models.Analytics.WishlistCount>>> GetTopCharactersAsync(int count)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var top = await db.WishlistCountData.AsQueryable().OrderByDescending(x => x.Count).ToListAsync();
                if (top == null)
                {
                    return "Not found".ToResponse(404);
                }
                return top.Take(count).ToList();
            }
        }

        /// <summary>
        /// Pobiera profil użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <returns>profil</returns>
        /// <response code="404">User not found</response>
        [HttpGet("user/{id}/profile")]
        public async Task<ActionResult<UserSiteProfile>> GetUserWaifuProfileAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.Users.AsQueryable().AsSplitQuery().Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Cards).ThenInclude(x => x.ArenaStats).Include(x => x.GameDeck)
                    .ThenInclude(x => x.Cards).ThenInclude(x => x.TagList).AsNoTracking().FirstOrDefaultAsync();

                if (user == null)
                {
                    return "User not found".ToResponse(404);
                }

                if (user.IsBlacklisted)
                {
                    return "User on blacklist".ToResponse(401);
                }

                var tagList = new List<string>();
                var tags = user.GameDeck.Cards.Where(x => x.TagList != null).Select(x => x.TagList.Select(c => c.Name));
                foreach(var tag in tags) tagList.AddRange(tag);

                var cardCount = new Dictionary<string, long>
                {
                    {Rarity.SSS.ToString(), user.GameDeck.Cards.Count(x => x.Rarity == Rarity.SSS)},
                    {Rarity.SS.ToString(),  user.GameDeck.Cards.Count(x => x.Rarity == Rarity.SS)},
                    {Rarity.S.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.S)},
                    {Rarity.A.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.A)},
                    {Rarity.B.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.B)},
                    {Rarity.C.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.C)},
                    {Rarity.D.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.D)},
                    {Rarity.E.ToString(),   user.GameDeck.Cards.Count(x => x.Rarity == Rarity.E)},
                    {"max",                 user.GameDeck.MaxNumberOfCards},
                    {"total",               user.GameDeck.Cards.Count}
                };

                var wallet = new Dictionary<string, long>
                {
                    {"PC", user.GameDeck.PVPCoins},
                    {"CT", user.GameDeck.CTCnt},
                    {"AC", user.AcCnt},
                    {"TC", user.TcCnt},
                    {"SC", user.ScCnt},
                };

                return new UserSiteProfile()
                {
                    Wallet = wallet,
                    CardsCount = cardCount,
                    Karma = user.GameDeck.Karma,
                    TagList = tagList.Distinct().ToList(),
                    UserTitle = user.GameDeck.GetUserNameStatus(),
                    Waifu = user.GameDeck.GetWaifuCard().ToView(),
                    ForegroundColor = user.GameDeck.ForegroundColor,
                    ForegroundPosition = user.GameDeck.ForegroundPosition,
                    BackgroundPosition = user.GameDeck.BackgroundPosition,
                    ExchangeConditions = user.GameDeck.ExchangeConditions,
                    BackgroundImageUrl = user.GameDeck.BackgroundImageUrl,
                    ForegroundImageUrl = user.GameDeck.ForegroundImageUrl,
                    Expeditions = user.GameDeck.Cards.Where(x => x.Expedition != CardExpedition.None).ToExpeditionView(user.GameDeck.Karma),
                    Gallery = user.GameDeck.Cards.Where(x => x.HasTag("galeria")).Take(user.GameDeck.CardsInGallery)
                        .OrderBy(x => x.Rarity).ThenByDescending(x => x.Quality).ThenBy(x => !x.IsAnimatedImage).ThenBy(x => x.Character).ToView(id)
                };
            }
        }

        /// <summary>
        /// Zastępuje id postaci w kartach
        /// </summary>
        /// <param name="oldId">id postaci z bazy shindena, która została usunięta</param>
        /// <param name="newId">id nowej postaci z bazy shindena</param>
        /// <response code="500">New character ID is invalid!</response>
        [HttpPost("character/repair/{oldId}/{newId}"), Authorize(Policy = "Site")]
        public async Task<ActionResult> RepairCardsAsync(ulong oldId, ulong newId)
        {
            var response = await _shClient.GetCharacterInfoAsync(newId);
            if (!response.IsSuccessStatusCode())
            {
                return "New character ID is invalid!".ToResponse(500);
            }

            var exe = new Executable($"api-repair oc{oldId} c{newId}", new Func<Task>(async () =>
            {
                using (var db = new Database.DatabaseContext(_config))
                {
                    var userRelease = new List<string>() { "users" };
                    var cards = db.Cards.AsQueryable().AsSplitQuery().Where(x => x.Character == oldId);

                    foreach (var card in cards)
                    {
                        card.Character = newId;
                        userRelease.Add($"user-{card.GameDeckId}");
                    }

                    await db.SaveChangesAsync();

                    QueryCacheManager.ExpireTag(userRelease.ToArray());
                }
            }), Priority.High);

            await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
            return "Success".ToResponse(200);
        }

        /// <summary>
        /// Podmienia dane na karcie danej postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <param name="newData">nowe dane karty</param>
        [HttpPost("cards/character/{id}/update"), Authorize(Policy = "Site")]
        public async Task<ActionResult> UpdateCardInfoAsync(ulong id, [FromBody]Models.CharacterCardInfoUpdate newData)
        {
            var exe = new Executable($"update cards-{id} img", new Func<Task>(async () =>
            {
                using (var db = new Database.DatabaseContext(_config))
                {
                    var userRelease = new List<string>() { "users" };
                    var cards = db.Cards.AsQueryable().AsSplitQuery().Where(x => x.Character == id);

                    foreach (var card in cards)
                    {
                        if (newData?.ImageUrl != null)
                            card.Image = newData.ImageUrl;

                        if (newData?.CharacterName != null)
                            card.Name = newData.CharacterName;

                        if (newData?.CardSeriesTitle != null)
                            card.Title = newData.CardSeriesTitle;

                        try
                        {
                            _waifu.DeleteCardImageIfExist(card);
                            await _waifu.GenerateAndSaveCardAsync(card);
                        }
                        catch (Exception) { }

                        userRelease.Add($"user-{card.GameDeckId}");
                    }

                    await db.SaveChangesAsync();

                    QueryCacheManager.ExpireTag(userRelease.ToArray());
                }
            }), Priority.High);

            await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
            return "Started!".ToResponse(200);
        }

        /// <summary>
        /// Generuje na nowo karty danej postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <response code="404">Character not found</response>
        /// <response code="405">Image in character date not found</response>
        [HttpPost("users/make/character/{id}"), Authorize(Policy = "Site")]
        public async Task<ActionResult> GenerateCharacterCardAsync(ulong id)
        {
            var response = await _shClient.GetCharacterInfoAsync(id);
            if (!response.IsSuccessStatusCode())
            {
                return "Character not found!".ToResponse(404);
            }

            if (!response.Body.HasImage)
            {
                return "There is no character image!".ToResponse(405);
            }

            var exe = new Executable($"update cards-{id}", new Func<Task>(async () =>
                {
                    using (var db = new Database.DatabaseContext(_config))
                    {
                        var userRelease = new List<string>() { "users" };
                        var cards = db.Cards.AsQueryable().AsSplitQuery().Where(x => x.Character == id);

                        foreach (var card in cards)
                        {
                            card.Image = response.Body.PictureUrl;

                            try
                            {
                                _waifu.DeleteCardImageIfExist(card);
                                await _waifu.GenerateAndSaveCardAsync(card);
                            }
                            catch (Exception) { }

                            userRelease.Add($"user-{card.GameDeckId}");
                        }

                        await db.SaveChangesAsync();

                        QueryCacheManager.ExpireTag(userRelease.ToArray());
                    }
                }));

            await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
            return "Started!".ToResponse(200);
        }

        /// <summary>
        /// Pobiera listę życzeń użytkownika
        /// </summary>
        /// <param name="id">id użytkownika discorda</param>
        /// <response code="404">User not found</response>
        [HttpGet("user/discord/{id}/wishlist"), Authorize(Policy = "Site")]
        public async Task<ActionResult<IEnumerable<Database.Models.Card>>> GetUserWishlistAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.GetCachedFullUserAsync(id);
                return await GetCardsFormWishlistAsync(user, db);
            }
        }

        /// <summary>
        /// Pobiera listę życzeń użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <response code="404">User not found</response>
        [HttpGet("user/shinden/{id}/wishlist"), Authorize(Policy = "Site")]
        public async Task<ActionResult<IEnumerable<Database.Models.Card>>> GetShindenUserWishlistAsync(ulong id)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.GetCachedFullUserByShindenIdAsync(id);
                return await GetCardsFormWishlistAsync(user, db);
            }
        }

        /// <summary>
        /// Pobiera liste kart z danym tagiem
        /// </summary>
        /// <param name="tag">tag na karcie</param>
        [HttpGet("cards/tag/{tag}"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<Database.Models.Card>> GetCardsWithTagAsync(string tag)
        {
            using (var db = new Database.DatabaseContext(_config))
            {
                return await db.Cards.Include(x => x.ArenaStats).Include(x => x.TagList).Where(x => x.TagList.Any(c => c.Name.Equals(tag, StringComparison.CurrentCultureIgnoreCase))).AsNoTracking().ToListAsync();
            }
        }

        /// <summary>
        /// Wymusza na bocie wygenerowanie obrazka jeśli nie istnieje
        /// </summary>
        /// <param name="id">id karty (wid)</param>
        /// <response code="403">Card already exist</response>
        /// <response code="404">Card not found</response>
        /// <response code="500">Card not generated</response>
        [HttpGet("card/{id}")]
        public async Task<ActionResult> GetCardAsync(ulong id)
        {
            bool miniature = System.IO.File.Exists($"{Services.Dir.CardsMiniatures}/{id}.webp") || System.IO.File.Exists($"{Services.Dir.CardsMiniatures}/{id}.gif");
            bool normal = System.IO.File.Exists($"{Services.Dir.Cards}/{id}.webp") || System.IO.File.Exists($"{Services.Dir.Cards}/{id}.gif");
            bool profile = System.IO.File.Exists($"{Services.Dir.CardsInProfiles}/{id}.webp") || System.IO.File.Exists($"{Services.Dir.CardsInProfiles}/{id}.gif");

            if (!miniature || !normal || !profile)
            {
                using (var db = new Database.DatabaseContext(_config))
                {
                    var card = await db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                    if (card == null)
                    {
                        return "Card not found!".ToResponse(404);
                    }

                    _waifu.DeleteCardImageIfExist(card);
                    var cardImage = await _waifu.GenerateAndSaveCardAsync(card, CardImageType.Normal, true);
                    if (!System.IO.File.Exists(cardImage))
                    {
                        return "Card not generated!".ToResponse(500);
                    }

                    return File(await System.IO.File.ReadAllBytesAsync(cardImage), $"image/{cardImage.Split('.').Last()}");
                }
            }
            else
            {
                return "Card already exist!".ToResponse(403);
            }
        }

        /// <summary>
        /// Daje użytkownikowi pakiety kart
        /// </summary>
        /// <param name="id">id użytkownika discorda</param>
        /// <param name="boosterPacks">model pakietu</param>
        /// <returns>użytkownik bota</returns>
        /// <response code="404">User not found</response>
        /// <response code="500">Model is Invalid</response>
        [HttpPost("discord/{id}/boosterpack"), Authorize(Policy = "Site")]
        public async Task<ActionResult> GiveUserAPacksAsync(ulong id, [FromBody]List<Models.CardBoosterPack> boosterPacks)
        {
            var packs = await ValidateBoosterPackAsync(ControllerContext, boosterPacks);
            if (packs.IsNullOrEmpty()) return null;

            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.GetCachedFullUserAsync(id);
                if (user == null)
                {
                    return "User not found!".ToResponse(404);
                }

                var exe = new Executable($"api-packet u{id}", new Func<Task>(async () =>
                {
                    using (var dbs = new Database.DatabaseContext(_config))
                    {
                        var botUser = await dbs.GetUserOrCreateAsync(id);

                        foreach (var pack in packs)
                            botUser.GameDeck.BoosterPacks.Add(pack);

                        await dbs.SaveChangesAsync();

                        QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                    }
                }));

                await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
                return "Boosterpack added!".ToResponse(200);
            }
        }

        /// <summary>
        /// Daje użytkownikowi pakiety kart
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <param name="boosterPacks">model pakietu</param>
        /// <returns>użytkownik bota</returns>
        /// <response code="404">User not found</response>
        /// <response code="500">Model is Invalid</response>
        [HttpPost("shinden/{id}/boosterpack"), Authorize(Policy = "Site")]
        public async Task<ActionResult<UserWithToken>> GiveShindenUserAPacksAsync(ulong id, [FromBody]List<Models.CardBoosterPack> boosterPacks)
        {
            var packs = await ValidateBoosterPackAsync(ControllerContext, boosterPacks);
            if (packs.IsNullOrEmpty()) return null;

            using (var db = new Database.DatabaseContext(_config))
            {
                var user = await db.GetCachedFullUserByShindenIdAsync(id);
                if (user == null)
                {
                    return "User not found!".ToResponse(404);
                }

                var discordId = user.Id;
                var exe = new Executable($"api-packet u{discordId}", new Func<Task>(async () =>
                {
                    using (var dbs = new Database.DatabaseContext(_config))
                    {
                        var botUser = await dbs.GetUserOrCreateAsync(discordId);

                        foreach (var pack in packs)
                            botUser.GameDeck.BoosterPacks.Add(pack);

                        await dbs.SaveChangesAsync();

                        QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                    }
                }));

                await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));

                TokenData tokenData = null;
                var currUser = ControllerContext.HttpContext.User;
                if (currUser.HasClaim(x => x.Type == ClaimTypes.Webpage))
                {
                    tokenData = UserTokenBuilder.BuildUserToken(_config, user, _time);
                }

                return new UserWithToken()
                {
                    Expire = tokenData?.Expire,
                    Token = tokenData?.Token,
                    User = user,
                };
            }
        }

        /// <summary>
        /// Otwiera pakiety i dodaje użytkownikowi karty wylosowane z nich
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <param name="boosterPacks">model pakietu</param>
        /// <returns>karty</returns>
        /// <response code="404">User not found</response>
        /// <response code="406">User has no space</response>
        /// <response code="500">Model/Data is Invalid</response>
        /// <response code="503">Command queue is full</response>
        [HttpPost("shinden/{id}/boosterpack/open"), Authorize(Policy = "Site")]
        public async Task<ActionResult<List<Card>>> GiveShindenUserAPacksAndOpenAsync(ulong id, [FromBody]List<Models.CardBoosterPack> boosterPacks)
        {
            if (boosterPacks?.Count < 1)
            {
                return "Model is Invalid".ToResponse(500);
            }

            var packs = new List<BoosterPack>();
            foreach (var pack in boosterPacks)
            {
                var rPack = pack.ToRealPack();
                if (rPack != null) packs.Add(rPack);
            }

            if (packs.Count < 1)
            {
                return "Data is Invalid".ToResponse(500);
            }

            ulong discordId = 0;
            using (var db = new Database.DatabaseContext(_config))
            {
                var bUser = await db.Users.AsQueryable().Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Cards).AsNoTracking().AsSplitQuery().FirstOrDefaultAsync();
                if (bUser == null)
                {
                    return "User not found".ToResponse(404);
                }
                if (bUser.GameDeck.Cards.Count + packs.Sum(x => x.CardCnt) > bUser.GameDeck.MaxNumberOfCards)
                {
                    return "User has no space left in deck".ToResponse(406);
                }
                discordId = bUser.Id;
            }

            var cards = new List<Card>();
            foreach (var pack in packs)
            {
                cards.AddRange(await _waifu.OpenBoosterPackAsync(null, pack));
            }

            var exe = new Executable($"api-packet-open u{discordId}", new Func<Task>(async () =>
            {
                using (var db = new Database.DatabaseContext(_config))
                {
                    var botUser = await db.GetUserOrCreateAsync(discordId);

                    botUser.Stats.OpenedBoosterPacks += packs.Count;

                    await UpdateWishlistCountAsync(db, cards, botUser);

                    await db.SaveChangesAsync();

                    QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                }
            }));

            if (!await _executor.TryAdd(exe, TimeSpan.FromSeconds(1)))
            {
                return "Command queue is full".ToResponse(503);
            }

            exe.Wait();

            return cards;
        }

        /// <summary>
        /// Otwiera pakiet użytkownika (wymagany Bearer od użytkownika)
        /// </summary>
        /// <param name="packNumber">numer pakietu</param>
        /// <response code="403">The appropriate claim was not found</response>
        /// <response code="404">User not found</response>
        /// <response code="406">User has no space</response>
        [HttpPost("boosterpack/open/{packNumber}"), Authorize(Policy = "Player")]
        public async Task<ActionResult<List<Card>>> OpenAPackAsync(int packNumber)
        {
            var currUser = ControllerContext.HttpContext.User;
            if (currUser.HasClaim(x => x.Type == "DiscordId"))
            {
                if (ulong.TryParse(currUser.Claims.First(x => x.Type == "DiscordId").Value, out var discordId))
                {
                    string bPackName = "";
                    var cards = new List<Card>();
                    using (var db = new Database.DatabaseContext(_config))
                    {
                        var botUserCh = await db.GetCachedFullUserAsync(discordId);
                        if (botUserCh == null)
                        {
                            return "User not found!".ToResponse(404);
                        }

                        if (botUserCh.GameDeck.BoosterPacks.Count < packNumber || packNumber <= 0)
                        {
                            return "Boosterpack not found!".ToResponse(404);
                        }

                        var pack = botUserCh.GameDeck.BoosterPacks.ToArray()[packNumber - 1];

                        if (botUserCh.GameDeck.Cards.Count + pack.CardCnt > botUserCh.GameDeck.MaxNumberOfCards)
                        {
                            return "User has no space left in deck!".ToResponse(406);
                        }

                        cards = await _waifu.OpenBoosterPackAsync(null, pack);
                        bPackName = pack.Name;
                    }

                    var exe = new Executable($"api-packet-open u{discordId}", new Func<Task>(async () =>
                    {
                        using (var db = new Database.DatabaseContext(_config))
                        {
                            var botUser = await db.GetUserOrCreateAsync(discordId);

                            var bPack = botUser.GameDeck.BoosterPacks.ToArray()[packNumber - 1];
                            if (bPack?.Name != bPackName)
                            {
                                return;
                            }

                            botUser.GameDeck.BoosterPacks.Remove(bPack);

                            if (bPack.CardSourceFromPack == CardSource.Activity || bPack.CardSourceFromPack == CardSource.Migration)
                            {
                                botUser.Stats.OpenedBoosterPacksActivity += 1;
                            }
                            else
                            {
                                botUser.Stats.OpenedBoosterPacks += 1;
                            }

                            await UpdateWishlistCountAsync(db, cards, botUser);

                            await db.SaveChangesAsync();

                            QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                        }
                    }));

                    await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));

                    exe.Wait();

                    return cards;
                }
            }
            return "The appropriate claim was not found".ToResponse(403);
        }

        /// <summary>
        /// Aktywuje lub dezaktywuje kartę (wymagany Bearer od użytkownika)
        /// </summary>
        /// <param name="wid">id karty</param>
        /// <response code="403">The appropriate claim was not found</response>
        /// <response code="404">Card not found</response>
        [HttpPut("deck/toggle/card/{wid}"), Authorize(Policy = "Player")]
        public async Task<ActionResult> ToggleCardStatusAsync(ulong wid)
        {
            var currUser = ControllerContext.HttpContext.User;
            if (currUser.HasClaim(x => x.Type == "DiscordId"))
            {
                if (ulong.TryParse(currUser.Claims.First(x => x.Type == "DiscordId").Value, out var discordId))
                {
                    using (var db = new Database.DatabaseContext(_config))
                    {
                        var botUserCh = await db.GetCachedFullUserAsync(discordId);
                        if (botUserCh == null)
                        {
                            return "User not found!".ToResponse(404);
                        }

                        var thisCardCh = botUserCh.GameDeck.Cards.FirstOrDefault(x => x.Id == wid);
                        if (thisCardCh == null)
                        {
                            return "Card not found!".ToResponse(404);
                        }

                        if (thisCardCh.InCage)
                        {
                            return "Card is in cage!".ToResponse(403);
                        }
                    }

                    var exe = new Executable($"api-deck u{discordId}", new Func<Task>(async () =>
                    {
                        using (var db = new Database.DatabaseContext(_config))
                        {
                            var botUser = await db.GetUserOrCreateAsync(discordId);
                            var thisCard = botUser.GameDeck.Cards.FirstOrDefault(x => x.Id == wid);
                            thisCard.Active = !thisCard.Active;

                            await db.SaveChangesAsync();

                            QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                        }
                    }));

                    await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
                    return "Card status toggled".ToResponse(200);
                }
            }
            return "The appropriate claim was not found".ToResponse(403);
        }

        private async Task<List<BoosterPack>> ValidateBoosterPackAsync(ControllerContext context, List<Models.CardBoosterPack> boosterPacks)
        {
            if (boosterPacks?.Count < 1)
            {
                await "Model is Invalid".ToResponse(500).ExecuteResultAsync(context);
                return null;
            }

            var packs = new List<BoosterPack>();
            foreach (var pack in boosterPacks)
            {
                var rPack = pack.ToRealPack();
                if (rPack != null) packs.Add(rPack);
            }

            if (packs.Count < 1)
            {
                await "Data is Invalid".ToResponse(500).ExecuteResultAsync(context);
                return null;
            }

            return packs;
        }

        private async Task<ActionResult<IEnumerable<Card>>> GetCardsFormWishlistAsync(User user, Database.DatabaseContext db)
        {
            if (user == null)
            {
                return "User not found!".ToResponse(404);
            }

            if (user.GameDeck.Wishes.Count < 1)
            {
                return "Wishlist not found!".ToResponse(404);
            }

            var p = user.GameDeck.GetCharactersWishList();
            var t = user.GameDeck.GetTitlesWishList();
            var c = user.GameDeck.GetCardsWishList();

            return (await _waifu.GetCardsFromWishlistAsync(c, p, t, db, user.GameDeck.Cards)).ToList();
        }

        private async Task UpdateWishlistCountAsync(Database.DatabaseContext db, List<Card> cards, User user)
        {
            var allWWCnt = await db.WishlistCountData.AsQueryable().AsNoTracking().ToListAsync();

            foreach (var card in cards)
            {
                card.Affection += user.GameDeck.AffectionFromKarma();
                card.FirstIdOwner = user.Id;

                var wwc = allWWCnt.FirstOrDefault(x => x.Id == card.Character);
                card.WhoWantsCount = wwc?.Count ?? 0;

                user.GameDeck.Cards.Add(card);
                await user.GameDeck.RemoveCharacterFromWishListAsync(card.Character, db);
            }
        }

        private List<Card> FilterCardsByTags(List<Card> cards, CardsQueryFilter filter)
        {
            if (filter.IncludeTags != null && filter.IncludeTags.Count > 0)
            {
                if (filter.FilterTagsMethod == FilterTagsMethodType.And)
                {
                    foreach (var iTag in filter.IncludeTags)
                        cards = cards.Where(x => x.HasTag(iTag)).ToList();
                }
                else
                {
                    cards = cards.Where(x => x.HasAnyTag(filter.IncludeTags)).ToList();
                }
            }

            if (filter.ExcludeTags != null)
            {
                foreach (var eTag in filter.ExcludeTags)
                    cards = cards.Where(x => !x.HasTag(eTag)).ToList();
            }

            return cards;
        }
    }
}