﻿#pragma warning disable 1591

using System.Collections.Generic;
using System.Linq;
using Sanakan.Database.Models;

namespace Sanakan.Api.Models
{
    public enum OrderType
    {
        Id, IdDes, Name, NameDes, Rarity, RarityDes, Title, TitleDes, Health, HealthDes, HealthBase, HealthBaseDes,
        Atack, AtackDes, Defence, DefenceDes, Exp, ExpDes, Dere, DereDes, Picture, PictureDes, Relation, RelationDes,
        CardPower, CardPowerDes, WhoWantsCount, WhoWantsCountDes, Blocked, BlockedDes
    }

    public enum FilterTagsMethodType
    {
        And, Or
    }

    /// <summary>
    /// Filtrowanie listy kart
    /// </summary>
    public class CardsQueryFilter
    {
        /// <summary>
        /// Sortowanie po parametrze
        /// </summary>
        public OrderType OrderBy { get; set; }
        /// <summary>
        /// Tekst wyszukiwania
        /// </summary>
        public string SearchText { get; set; }
        /// <summary>
        /// Tagi jakie ma zawierać karta
        /// </summary>
        public List<string> IncludeTags { get; set; }
        /// <summary>
        /// Tagi jakich karta ma nie mieć
        /// </summary>
        public List<string> ExcludeTags { get; set; }
        /// <summary>
        /// W jaki sposów filtrować po tagach
        /// </summary>
        public FilterTagsMethodType FilterTagsMethod { get; set; }

        public static IQueryable<Card> Use(OrderType type, IQueryable<Card> query)
        {
            switch (type)
            {
                case OrderType.Atack:
                    return query.OrderBy(x => x.Attack + x.AttackBonus + (x.RestartCnt * 2d));
                case OrderType.AtackDes:
                    return query.OrderByDescending(x => x.Attack + x.AttackBonus + (x.RestartCnt * 2d));
                case OrderType.Exp:
                    return query.OrderBy(x => x.ExpCnt);
                case OrderType.ExpDes:
                    return query.OrderByDescending(x => x.ExpCnt);
                case OrderType.Dere:
                    return query.OrderBy(x => x.Dere);
                case OrderType.DereDes:
                    return query.OrderByDescending(x => x.Dere);
                case OrderType.Defence:
                    return query.OrderBy(x => x.Defence + x.DefenceBonus + x.RestartCnt);
                case OrderType.DefenceDes:
                    return query.OrderByDescending(x => x.Defence + x.DefenceBonus + x.RestartCnt);
                case OrderType.Health:
                    return query.OrderBy(x => x.Health + ((x.Health * (x.Affection * 5d / 100d)) + x.HealthBonus));
                case OrderType.HealthDes:
                    return query.OrderByDescending(x => x.Health + ((x.Health * (x.Affection * 5d / 100d)) + x.HealthBonus));
                case OrderType.HealthBase:
                    return query.OrderBy(x => x.Health);
                case OrderType.HealthBaseDes:
                    return query.OrderByDescending(x => x.Health);
                case OrderType.CardPower:
                    return query.OrderBy(x => x.CardPower);
                case OrderType.CardPowerDes:
                    return query.OrderByDescending(x => x.CardPower);
                case OrderType.WhoWantsCount:
                    return query.OrderBy(x => x.WhoWantsCount);
                case OrderType.WhoWantsCountDes:
                    return query.OrderByDescending(x => x.WhoWantsCount);
                case OrderType.Relation:
                    return query.OrderBy(x => x.Affection);
                case OrderType.RelationDes:
                    return query.OrderByDescending(x => x.Affection);
                case OrderType.Title:
                    return query.OrderBy(x => x.Title);
                case OrderType.TitleDes:
                    return query.OrderByDescending(x => x.Title);
                case OrderType.RarityDes:
                    return query.OrderBy(x => x.Rarity).ThenByDescending(x => x.Quality);
                case OrderType.Rarity:
                    return query.OrderByDescending(x => x.Rarity).ThenBy(x => x.Quality);
                case OrderType.Name:
                    return query.OrderBy(x => x.Name);
                case OrderType.NameDes:
                    return query.OrderByDescending(x => x.Name);
                case OrderType.Picture:
                    return query.OrderBy(x => (x.CustomImage == null ? (x.Image == null ? 0 : 1) : 2));
                case OrderType.PictureDes:
                    return query.OrderByDescending(x => (x.CustomImage == null ? (x.Image == null ? 0 : 1) : 2));
                case OrderType.Blocked:
                    return query.OrderBy(x => x.IsTradable ? 1 : 0);
                case OrderType.BlockedDes:
                    return query.OrderByDescending(x => x.IsTradable ? 1: 0);
                case OrderType.IdDes:
                    return query.OrderByDescending(x => x.Id);

                default:
                case OrderType.Id:
                    return query;
            }
        }
    }
}