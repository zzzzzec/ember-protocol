"""Compile specialist cash and barter listings into native trader and quest data."""
import argparse
import copy
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROUBLES = "5449016a4bdc2d6f028b456f"


def read(path):
    return json.loads(path.read_text("utf-8-sig"))


def stable_id(kind, key):
    return hashlib.sha256(f"ember-protocol:{kind}:{key}".encode()).hexdigest()[:24]


def reward_id(oid):
    return stable_id("procurement-reward", oid)


def specialist_entries(stock, contacts):
    assert stock["schemaVersion"] == 1 and set(stock["traders"]) == set(contacts)
    for trader, groups in stock["traders"].items():
        for group in groups:
            for offer in group["offers"]:
                yield dict(offer, id=stable_id("specialist-stock", offer["key"]), owner=trader,
                           unlock=group["quest"], level=group["level"], limit=offer.get("limit", 1),
                           validatePayment=True)


def compile_procurement(write=False):
    data = ROOT / "content/server/data"
    cfg = read(ROOT / "config/procurement.json")
    stock = read(ROOT / "config/specialist-stock.json")
    assert cfg["schemaVersion"] == 1 and cfg["cashMarkup"] >= 1 and cfg["roundToRoubles"] > 0
    paths = {name: data / (name + ".json") for name in ("trader", "manifest", "opening", "quests")}
    files = {name: read(path) for name, path in paths.items()}
    original = copy.deepcopy(files)
    trader, manifest, quests = (files[name] for name in ("trader", "manifest", "quests"))
    contacts = {c["name"].lower(): c for c in files["opening"]["contacts"]}
    legacy_owner = next(name for name, contact in contacts.items() if contact["id"] == cfg["traderId"])
    assert len({row["tpl"] for row in cfg["cash"]}) == len(cfg["cash"])
    legacy = []
    for row in cfg["cash"] + cfg["barters"]:
        assert row["stock"] == row["limit"] == 1
        price = round(row["nativeReference"] * cfg["cashMarkup"] / cfg["roundToRoubles"]) * cfg["roundToRoubles"] if "nativeReference" in row else 0
        legacy.append(dict(row, owner=legacy_owner, price=price))
    specialists = list(specialist_entries(stock, contacts))
    specialist_ids = [row["id"] for row in specialists]
    entries = legacy + specialists
    ids = {row["id"] for row in entries}
    assert len(ids) == len(entries)

    previous_specialists = set(manifest.get("specialistStock", []))
    managed = previous_specialists | {row["id"] for row in legacy}
    assert not (set(specialist_ids) - previous_specialists) & {row["id"] for row in manifest["offers"]}
    supply_ids = set(manifest["supplyRoutes"]["offers"])
    specialist_owners = {name: [] for name in contacts}
    assort = trader["assort"]
    assort["items"] = [row for row in assort["items"] if row["_id"] not in managed and row.get("parentId") not in managed]
    for mapping in (assort["barter_scheme"], assort["loyal_level_items"], trader["questassort"]["success"]):
        for oid in managed:
            mapping.pop(oid, None)
    for key in ("offers", "barters"):
        manifest[key] = [row for row in manifest[key] if row["id"] not in managed]
    reward_ids = {reward_id(oid) for oid in managed}
    for quest in quests.values():
        quest["rewards"]["Success"] = [row for row in quest["rewards"]["Success"] if row["id"] not in reward_ids]

    for row in entries:
        oid, tpl, level, limit = (row[key] for key in ("id", "tpl", "level", "limit"))
        owner = contacts[row["owner"]]
        price, inputs = row.get("price", 0), row.get("inputs", [])
        assert all(re.fullmatch("[0-9a-f]{24}", key) for key in (oid, tpl))
        assert type(level) is int and 1 <= level <= 4 and type(limit) is int and 1 <= limit <= 16
        assert type(price) is int and (price > 0) != bool(inputs)
        assert len({part["_tpl"] for part in inputs}) == len(inputs)
        assert all(re.fullmatch("[0-9a-f]{24}", part["_tpl"]) and part["_tpl"] not in (ROUBLES, tpl)
                   and type(part["count"]) is int and 1 <= part["count"] <= 12 for part in inputs)
        # Root stack count is the number in stock. Each purchase yields one item.
        upd = dict(StackObjectsCount=limit, UnlimitedCount=False, BuyRestrictionMax=limit, BuyRestrictionCurrent=0)
        if "foodResource" in row:
            upd["FoodDrink"] = dict(HpPercent=row["foodResource"])
        root = dict(_id=oid, _tpl=tpl, parentId="hideout", slotId="hideout", upd=upd)
        parts = row.get("parts", [])
        assert len({part["slotId"] for part in parts}) == len(parts)
        children = [dict(part, _id=stable_id("specialist-part", oid + ":" + part["slotId"]), parentId=oid) for part in parts]
        assort["items"].extend([root, *children])
        assort["barter_scheme"][oid] = [[dict(_tpl=ROUBLES, count=price)] if price else inputs]
        assort["loyal_level_items"][oid] = level
        qid = row.get("unlock")
        meta = dict(id=oid, tpl=tpl, name=row["name"], price=price, stock=limit, limit=limit, level=level, unlock=qid)
        if row.get("validatePayment"):
            meta["validatePayment"] = True
            specialist_owners[row["owner"]].append(oid)
        elif oid not in owner["offers"]:
            owner["offers"].append(oid)
        if inputs:
            meta["barter"] = inputs
            manifest["barters"].append(copy.deepcopy(meta))
        manifest["offers"].append(meta)
        if qid:
            assert qid in quests and quests[qid]["traderId"] == owner["id"], (row["name"], qid)
            trader["questassort"]["success"][oid] = qid
            reward_root = {key: val for key, val in root.items() if key not in ("parentId", "slotId")}
            reward = dict(id=reward_id(oid), type="AssortmentUnlock", isHidden=False, unknown=False,
                          gameMode=["regular", "pve"], availableInGameEditions=[], items=[reward_root, *children],
                          target=oid, traderId=owner["id"], loyaltyLevel=level)
            quests[qid]["rewards"]["Success"].append(reward)

    for name, contact in contacts.items():
        kept = [oid for oid in contact["offers"] if oid not in previous_specialists]
        index = next((i for i, oid in enumerate(kept) if oid in supply_ids), len(kept))
        contact["offers"] = kept[:index] + specialist_owners[name] + kept[index:]
    manifest["specialistStock"] = specialist_ids
    changed = [name for name in files if original[name] != files[name]]
    if write:
        for name in changed:
            paths[name].write_text(json.dumps(files[name], ensure_ascii=False, indent=2) + "\n", "utf-8")
    return dict(cashOffers=sum(row.get("price", 0) > 0 for row in entries),
                barters=sum(bool(row.get("inputs")) for row in entries), specialistOffers=len(specialists),
                changedFiles=changed, written=write)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    result = compile_procurement(args.write)
    print(json.dumps(result, indent=2))
    if result["changedFiles"] and not args.write:
        raise SystemExit("Compile procurement before packaging: tools/compile_procurement.py --write")
