"""Compile staged weapon trials, native shop trees, and localized objectives."""
import argparse
import copy
import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ROUBLES = "5449016a4bdc2d6f028b456f"
LEGACY_TEMPLATE = "4b73e556a053396410845282"


def stable(kind, key):
    return hashlib.sha256(f"ember-protocol:weapon-trial:{kind}:{key}".encode()).hexdigest()[:24]


def read(path):
    return json.loads(path.read_text("utf-8-sig"))


def insert_before(rows, extra, key, later):
    index = next((i for i, row in enumerate(rows) if key(row) in later), len(rows))
    return rows[:index] + extra + rows[index:]


def tree(stage, root_id, upd):
    ids = [root_id] + [stable("part", root_id + ":" + str(i)) for i in range(len(stage["assembly"]))]
    rows = [dict(_id=root_id, _tpl=stage["weapon"], upd=upd)]
    for index, (parent, slot, tpl) in enumerate(stage["assembly"], 1):
        assert 0 <= parent < index
        rows.append(dict(_id=ids[index], _tpl=tpl, parentId=ids[parent], slotId=slot))
    return rows


def reward(kind, key, **fields):
    return dict(id=stable("reward", key), type=kind, isHidden=False, unknown=False,
                gameMode=["regular", "pve"], availableInGameEditions=[], **fields)


def item_reward(key, items, count=1):
    return reward("Item", key, value=count, items=items, target=items[0]["_id"], findInRaid=False, isEncoded=False)


def starter_rewards(stage):
    if not stage["issued"]:
        return []
    key = stage["key"]
    gun = tree(stage, stable("item", key + ":issued-gun"),
               dict(StackObjectsCount=1, SpawnedInSession=False, Repairable=dict(Durability=90, MaxDurability=100)))
    box, ammo, count = stage["starterBox"]
    box_id = stable("item", key + ":starter-box")
    contents = [dict(_id=box_id, _tpl=box, upd=dict(StackObjectsCount=1, SpawnedInSession=False)),
                dict(_id=stable("item", key + ":starter-rounds"), _tpl=ammo, parentId=box_id, slotId="cartridges",
                     location=0, upd=dict(StackObjectsCount=count, SpawnedInSession=False))]
    return [item_reward(key + ":gun", gun), item_reward(key + ":ammo", contents)]


def counter(template, key, stage, extraction=False):
    c = copy.deepcopy(template)
    c["id"] = stable("condition", key)
    c["counter"]["id"] = stable("counter", key)
    c["value"] = 1 if extraction else stage["kills"]
    condition, location = c["counter"]["conditions"]
    location.update(id=stable("location", key), target=stage["maps"])
    if extraction:
        c["type"] = "Exploration"
        c["index"] = 1
        c["counter"]["conditions"][0] = dict(conditionType="ExitStatus", id=stable("exit", key),
                                               status=["Survived"], dynamicLocale=False)
    else:
        challenge, _, distance = stage["challenge"].partition(":")
        condition.update(id=stable("kill", key), weapon=[stage["weapon"]],
                         weaponModsInclusive=[sorted({row[2] for row in stage["assembly"]})],
                         weaponModsExclusive=[], bodyPart=["Head"] if challenge == "head" else [],
                         distance={"compareMethod": "<=" if challenge == "close" else ">=",
                                   "value": int(distance) if distance else 0})
    return c


def create_quest(template, stage, trader, prerequisite, image):
    key = stage["key"]
    qid = stable("quest", key)
    q = copy.deepcopy(template)
    for field, val in q.items():
        if isinstance(val, str) and val.startswith(template["_id"] + " "):
            q[field] = qid + val[len(template["_id"]):]
    q.update(_id=qid, QuestName=stage["title"], traderId=trader, image=image)
    start = [dict(copy.deepcopy(template["conditions"]["AvailableForStart"][0]),
                  id=stable("condition", key + ":prerequisite"), target=prerequisite)]
    if stage["level"] > 1:
        start.append(dict(conditionType="Level", id=stable("condition", key + ":level"), index=len(start),
                          compareMethod=">=", value=stage["level"], dynamicLocale=False, visibilityConditions=[]))
    if stage["loyalty"] > 1:
        start.append(dict(conditionType="TraderLoyalty", id=stable("condition", key + ":loyalty"), index=len(start),
                          compareMethod=">=", target=trader, value=stage["loyalty"],
                          dynamicLocale=False, visibilityConditions=[]))
    source_kill = template["conditions"]["AvailableForFinish"][0]
    objectives = [counter(source_kill, key, stage)]
    if stage["challenge"] == "survive":
        objectives.append(counter(source_kill, key + ":extract", stage, extraction=True))
    ret = copy.deepcopy(template["conditions"]["AvailableForFinish"][-1])
    mods = [part[2] for part in stage["assembly"]]
    ret.update(id=stable("return", key), index=len(objectives), target=[stage["weapon"]],
               containsItems=sorted(set(mods)), durability={"compareMethod": ">=", "value": 50},
               magazineCapacity={"compareMethod": ">=", "value": stage["magazineCapacity"]},
               visibilityConditions=[dict(conditionType="CompleteCondition", id=stable("visibility", c["id"]), target=c["id"])
                                     for c in objectives])
    q["conditions"] = dict(AvailableForStart=start, AvailableForFinish=[*objectives, ret], Fail=[])
    cash = stage["cashReward"]
    q["rewards"] = dict(Started=starter_rewards(stage), Fail=[], Success=[
        reward("Experience", key + ":xp", value=stage["xp"]),
        reward("TraderStanding", key + ":standing", target=trader, value=.025),
        item_reward(key + ":cash", [dict(_id=stable("item", key + ":cash"), _tpl=ROUBLES,
                                       upd=dict(StackObjectsCount=cash, SpawnedInSession=False))], cash)])
    spec = dict(weapon=stage["weapon"], requiredParts=sorted(set(mods)), exactParts=sorted(mods),
                minimumDurability=50, minimumMagazineCapacity=stage["magazineCapacity"],
                returnCondition=ret["id"], killCondition=stable("kill", key), referenceOffer=stable("offer", key))
    return q, spec


def write_locales(locales, q, stage, dialogue):
    key = stage["key"]
    qualifier, _, distance = stage["challenge"].partition(":")
    zh_tag = {"head": "爆头", "far": f"在 {distance} 米以外", "close": f"在 {distance} 米以内"}.get(qualifier, "")
    en_tag = {"head": " with headshots", "far": f" at least {distance} m away",
              "close": f" within {distance} m"}.get(qualifier, "")
    for lang, fields in (
        ("ch", [stage["title"], dialogue["description"], dialogue["accepted"], dialogue["success"]]),
        ("en", [f"Field trial {key}", "The signed build is on the trader's shelf. Use it in the field and bring back the unloaded assembly.",
                "The loan and a sealed box of cartridges are in your mail." if stage["issued"] else "The matching build is available to buy. This commission uses your own equipment.",
                "The assembly and field record are both here. The payment is ready."])):
        l = locales[lang]
        for field, val in zip(("name", "description", "startedMessageText", "successMessageText"), fields):
            l[q[field]] = val
        objectives = q["conditions"]["AvailableForFinish"]
        l[objectives[0]["id"]] = (f"在{stage['mapNames'][0]}用{stage['setup']}{zh_tag}击杀感染者"
                                  if lang == "ch" else f"Eliminate infected in {stage['mapNames'][1]} with the specified {stage['weaponName']} build{en_tag}")
        if qualifier == "survive":
            l[objectives[1]["id"]] = (f"从{stage['mapNames'][0]}成功撤离" if lang == "ch"
                                      else f"Survive and extract from {stage['mapNames'][1]}")
        l[objectives[-1]["id"]] = (f"交还{stage['setup']}，退净弹药，耐久至少 50"
                                   if lang == "ch" else f"Return the specified unloaded {stage['weaponName']} at 50 or more durability")


def compile_weapon_trials(write=False):
    data = ROOT / "content/server/data"
    names = ("quests", "trader", "manifest", "opening", "field-quests", "locales", "campaign")
    paths = {name: data / (name + ".json") for name in names}
    paths["art"] = ROOT / "content/narrative/quest-illustrations.json"
    source = read(ROOT / "config/weapon-trials.json")
    dialogue = read(ROOT / "content/narrative/weapon-trials.zh-CN.json")["quests"]
    assert source["schemaVersion"] == 1
    original = {name: read(path) for name, path in paths.items()}
    files = copy.deepcopy(original)
    quests, trader, manifest, opening, field, locales, campaign = (files[n] for n in names)
    art = files["art"]
    assets = {a["key"]: a["route"] + ".png" for a in art["assets"]}
    previous = manifest.get("weaponTrials", {"quests": [], "offers": [], "localeKeys": []})
    old_quests, old_offers = set(previous["quests"]), set(previous["offers"])
    for qid in old_quests:
        quests.pop(qid, None)
        opening["questRelease"].pop(qid, None)
        art["quests"].pop(qid, None)
        field["loans"].pop(qid, None)
        field["issuedOnce"].pop(qid, None)
    for key in previous["localeKeys"]:
        for lang in ("ch", "en"):
            locales[lang].pop(key, None)
    field["questIds"] = [qid for qid in field["questIds"] if qid not in old_quests]
    manifest["quests"] = [q for q in manifest["quests"] if q["id"] not in old_quests]
    manifest["offers"] = [o for o in manifest["offers"] if o["id"] not in old_offers]
    campaign["sideStories"] = [s for s in campaign["sideStories"] if not set(s["quests"]) & old_quests]
    for q in quests.values():
        q["rewards"]["Success"] = [r for r in q["rewards"]["Success"]
                                   if r["type"] != "AssortmentUnlock" or r.get("target") not in old_offers]
    assort = trader["assort"]
    old_items = set(old_offers)
    for item in assort["items"]:
        if item.get("parentId") in old_items:
            old_items.add(item["_id"])
    assort["items"] = [item for item in assort["items"] if item["_id"] not in old_items]
    for oid in old_offers:
        for mapping in (assort["barter_scheme"], assort["loyal_level_items"], trader["questassort"]["success"]):
            mapping.pop(oid, None)
    contacts = {c["id"]: c for c in opening["contacts"]}
    for contact in contacts.values():
        contact["offers"] = [oid for oid in contact["offers"] if oid not in old_offers]

    later = set(manifest["supplyRoutes"]["offers"]) | set(manifest["specialistStock"])
    proc = read(ROOT / "config/procurement.json")
    later.update(o["id"] for o in proc["cash"] + proc["barters"])
    new_quests, new_offers, new_items, locale_keys = [], [], [], []
    template = quests[LEGACY_TEMPLATE]
    assert {group["trader"] for group in source["groups"].values()} == set(contacts)
    assert all(group["stages"] for group in source["groups"].values())
    for character, group in source["groups"].items():
        owner = group["trader"]
        prerequisite = group["prerequisite"]
        group_quests, group_offers = [], []
        for stage in group["stages"]:
            q, spec = create_quest(template, stage, owner, prerequisite, assets[character])
            qid, key = q["_id"], stage["key"]
            assert qid not in quests and qid in dialogue and dialogue[qid]["speaker"] == owner
            quests[qid] = q
            write_locales(locales, q, stage, dialogue[qid])
            locale_keys.extend([q[k] for k in ("name", "description", "startedMessageText", "successMessageText")])
            locale_keys.extend(c["id"] for c in q["conditions"]["AvailableForFinish"])
            group_quests.append(qid)
            new_quests.append(qid)
            opening["questRelease"][qid] = prerequisite
            art["quests"][qid] = character
            field["questIds"].append(qid)
            field["loans"][qid] = spec
            if stage["issued"]:
                field["issuedOnce"][qid] = dict(kind="weapon")
            manifest["quests"].append(dict(id=qid, lane="weaponTrial", index=len(group_quests), kind=character,
                                           weapon=stage["weapon"], unlock=prerequisite))

            def stock(oid, tpl, name, price, limit, whole=False):
                upd = dict(StackObjectsCount=limit, UnlimitedCount=False, BuyRestrictionMax=limit, BuyRestrictionCurrent=0)
                bundle = tree(stage, oid, upd) if whole else [dict(_id=oid, _tpl=tpl, upd=upd)]
                if whole:
                    bundle[0]["upd"]["Repairable"] = dict(Durability=100, MaxDurability=100)
                reward_items = copy.deepcopy(bundle)
                bundle[0].update(parentId="hideout", slotId="hideout")
                assert oid not in assort["barter_scheme"] and price > 0
                new_items.extend(bundle)
                new_offers.append(dict(id=oid, tpl=tpl, name=name, price=price, stock=limit, limit=limit,
                                       level=stage["loyalty"], unlock=prerequisite, validatePayment=True))
                group_offers.append(oid)
                assort["barter_scheme"][oid] = [[dict(_tpl=ROUBLES, count=price)]]
                assort["loyal_level_items"][oid] = stage["loyalty"]
                trader["questassort"]["success"][oid] = prerequisite
                unlock = reward("AssortmentUnlock", oid, items=reward_items, target=oid, traderId=owner, loyaltyLevel=stage["loyalty"])
                rewards = quests[prerequisite]["rewards"]
                rewards["Success"] = insert_before(rewards["Success"], [unlock],
                                                   lambda row: row.get("target") if row["type"] == "AssortmentUnlock" else None, later)

            stock(spec["referenceOffer"], stage["weapon"], stage["weaponName"], stage["price"], 1, whole=True)
            for tpl, name, price, cap in stage["spares"]:
                stock(stable("offer", key + ":" + tpl), tpl, name, price, cap)
            prerequisite = qid
        contact = contacts[owner]
        contact["offers"] = insert_before(contact["offers"], group_offers, str, later)
        campaign["sideStories"].append(dict(title=group["title"], quests=group_quests))
    assert set(new_quests) == set(dialogue)
    manifest["offers"] = insert_before(manifest["offers"], new_offers, lambda row: row["id"], later)
    assort["items"] = insert_before(assort["items"], new_items, lambda row: row["_id"], later)
    for field_name in ("barter_scheme", "loyal_level_items"):
        entries = assort[field_name]
        assort[field_name] = {item["_id"]: entries[item["_id"]] for item in assort["items"] if item.get("parentId") == "hideout"}
    manifest["weaponTrials"] = dict(quests=new_quests, offers=[o["id"] for o in new_offers], localeKeys=locale_keys)
    changed = [name for name, content in files.items() if original[name] != content]
    if write:
        for name in changed:
            paths[name].write_text(json.dumps(files[name], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return dict(quests=len(new_quests), offers=len(new_offers), issued=sum(q in field["issuedOnce"] for q in new_quests), changedFiles=changed)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    result = compile_weapon_trials(args.write)
    print(json.dumps(result, indent=2))
    if result["changedFiles"] and not args.write:
        raise SystemExit("Compile weapon trials before packaging: tools/compile_weapon_trials.py --write")
