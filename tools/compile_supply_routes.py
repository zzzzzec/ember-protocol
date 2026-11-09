"""Compile authored hideout recipes and trader supply routes into native game data."""

import argparse
import copy
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AREAS = {"lavatory": 2, "collector": 6, "medstation": 7, "nutrition": 8, "workbench": 10, "intelligence": 11}
ROUBLES = "5449016a4bdc2d6f028b456f"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def stable_id(row):
    return hashlib.sha256(("ember-protocol:supply:" + row["key"]).encode()).hexdigest()[:24]


def compile_supply_routes(write=False):
    data = ROOT / "content/server/data"
    config = read(ROOT / "config/supply-routes.json")
    assert config["schemaVersion"] == 1
    catalog = config["catalog"]
    assert len(set(catalog.values())) == len(catalog)
    assert all(re.fullmatch("[0-9a-f]{24}", tpl) and tpl != ROUBLES for tpl in catalog.values())
    recipes, offers = config["crafts"], config["traders"]
    keys = [row["key"] for row in recipes + offers]
    assert len(set(keys)) == len(keys) and all(re.fullmatch("[a-z][a-z0-9_]+", key) for key in keys)
    for row in recipes + offers:
        ingredients = row.get("inputs", {})
        assert row["product"] in catalog and row["product"] not in ingredients
        assert all(key in catalog and type(count) is int and 1 <= count <= 8 for key, count in ingredients.items())
        assert type(row["level"]) is int and row["level"] >= 1

    names = ("recipes", "trader", "manifest", "opening")
    paths = {name: data / (name + ".json") for name in names}
    original = {name: read(path) if path.exists() else None for name, path in paths.items()}
    files = copy.deepcopy(original)
    assort = files["trader"]["assort"]
    manifest = files["manifest"]
    previous = manifest.get("supplyRoutes", {"recipes": [], "offers": []})
    previous_recipes, previous_offers = map(set, (previous["recipes"], previous["offers"]))
    generated_recipes, generated_offers = [], []
    contacts = {row["name"].lower(): row for row in files["opening"]["contacts"]}
    procurement = read(ROOT / "config/procurement.json")
    # Keep the independently compiled procurement catalogue at the tail of each shared list.
    procurement_ids = {row["id"] for row in procurement["cash"] + procurement["barters"]}
    procurement_ids.update(manifest.get("specialistStock", []))
    for item in assort["items"]:
        if item.get("parentId") in procurement_ids:
            procurement_ids.add(item["_id"])

    def merge(existing, entries, owned, id_key):
        preserved = [row for row in existing if row[id_key] not in owned]
        incoming = {row[id_key] for row in entries}
        assert not incoming.intersection(row[id_key] for row in preserved), "Supply ID conflicts with existing content"
        ordinary = [row for row in preserved if row[id_key] not in procurement_ids]
        return ordinary + entries + [row for row in preserved if row[id_key] in procurement_ids]

    for row in recipes:
        area = AREAS[row["area"]]
        assert row["level"] <= 3 and type(row["minutes"]) is int and 10 <= row["minutes"] <= 180
        assert row["inputs"] and type(row["count"]) is int and 1 <= row["count"] <= 2
        tools = row["tools"]
        assert len(set(tools)) == len(tools) and all(k in catalog and k not in row["inputs"] and k != row["product"] for k in tools)
        requirements = [{"type": "Area", "areaType": area, "requiredLevel": row["level"]}]
        requirements.extend({"type": "Item", "templateId": catalog[key], "count": count,
                             "isEncoded": False, "isFunctional": False, "isSpawnedInSession": False}
                            for key, count in row["inputs"].items())
        requirements.extend({"type": "Tool", "templateId": catalog[key]} for key in tools)
        generated_recipes.append(dict(
            _id=stable_id(row), areaType=area, requirements=requirements, productionTime=row["minutes"] * 60,
            endProduct=catalog[row["product"]], isEncoded=False, locked=False,
            needFuelForAllProductionTime=False, continuous=False, count=row["count"],
            productionLimitCount=0, isCodeProduction=False,
        ))

    for contact in contacts.values():
        contact["offers"] = [oid for oid in contact["offers"] if oid not in previous_offers]
    for oid in previous_offers:
        assort["barter_scheme"].pop(oid, None)
        assort["loyal_level_items"].pop(oid, None)
    roots, fence_transfers = [], {}
    for row in offers:
        merchant = row["trader"]
        assert merchant == "rin" or merchant in contacts
        assert row["level"] <= 4 and type(row["limit"]) is int and row["limit"] == 1
        price = row.get("price", 0)
        assert ("price" in row) != ("inputs" in row)
        assert (type(price) is int and price > 0) if "price" in row else bool(row["inputs"])
        oid, tpl = stable_id(row), catalog[row["product"]]
        scheme = ([{"_tpl": ROUBLES, "count": price}] if price else
                  [{"_tpl": catalog[key], "count": count} for key, count in row["inputs"].items()])
        offer = dict(id=oid, tpl=tpl, name=row["product"].replace("_", " "), price=price, stock=row["limit"],
                     limit=row["limit"], level=row["level"], unlock=None)
        if not price:
            offer["barter"] = scheme
        # These stable keys previously belonged to Fence. Carry over the current raid's quota.
        if row["key"].startswith("fence_"):
            assert "barter" in offer
            fence_transfers[oid] = contacts[merchant]["id"] if merchant in contacts else manifest["traderId"]
        roots.append(dict(_id=oid, _tpl=tpl, parentId="hideout", slotId="hideout",
                          upd=dict(StackObjectsCount=row["limit"], UnlimitedCount=False,
                                   BuyRestrictionMax=row["limit"], BuyRestrictionCurrent=0)))
        assort["barter_scheme"][oid] = [scheme]
        assort["loyal_level_items"][oid] = row["level"]
        if merchant in contacts:
            contacts[merchant]["offers"].append(oid)
        generated_offers.append(offer)

    files["recipes"] = merge(files["recipes"], generated_recipes, previous_recipes, "_id")
    assort["items"] = merge(assort["items"], roots, previous_offers, "_id")
    for key in ("barter_scheme", "loyal_level_items"):
        entries = assort[key]
        assort[key] = {oid: entries[oid] for oid in [r["_id"] for r in assort["items"] if r.get("parentId") == "hideout"]}
    manifest["offers"] = merge(manifest["offers"], generated_offers, previous_offers, "id")
    manifest["barters"] = merge(manifest["barters"], [r for r in generated_offers if "barter" in r], previous_offers, "id")
    manifest["supplyRoutes"] = {
        "recipes": [r["_id"] for r in generated_recipes],
        "offers": [r["id"] for r in generated_offers],
        "fenceTransfers": fence_transfers,
    }
    changed = [name for name in names if original[name] != files[name]]
    if write:
        for name in changed:
            paths[name].write_text(json.dumps(files[name], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return dict(crafts=len(recipes), offers=len(offers), fenceTransfers=len(fence_transfers), changedFiles=changed)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    result = compile_supply_routes(args.write)
    print(json.dumps(result, indent=2))
    if result["changedFiles"] and not args.write:
        raise SystemExit("Compile supply routes before packaging: tools/compile_supply_routes.py --write")
