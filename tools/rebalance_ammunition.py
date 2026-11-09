"""Reproduce authored ammunition prices from pinned game tables and explicit balance rules."""
from pathlib import Path
import argparse, collections, copy, hashlib, json, math

def read(p):return json.loads(p.read_text('utf-8-sig'))
def write(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n','utf-8')
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--game-root',type=Path,required=True);ap.add_argument('--report',type=Path,required=True);ap.add_argument('--write',action='store_true')
    a=ap.parse_args();repo=Path(__file__).resolve().parent.parent;data=repo/'content/server/data';rules=read(repo/'config/ammunition.json')
    native=a.game_root/'SPT/SPT_Data/database';items=read(native/'templates/items.json');original={i['Id']:i['Price'] for i in read(native/'templates/handbook.json')['Items']}
    manifest=read(data/'manifest.json');trader=read(data/'trader.json');quests=read(data/'quests.json');config=read(repo/'config/gameplay.json');recipes=read(data/'recipes.json')
    props={tpl:i['_props'] for tpl,i in items.items() if i.get('_props',{}).get('ammoType') in ['bullet','buckshot'] and tpl in original}
    for x in rules['additionalAmmo']:
        props[x['tpl']]=dict(Caliber=x['caliber'],Damage=x['damage'],PenetrationPower=x['penetration'],buckshotBullets=x['projectiles']);original[x['tpl']]=x['baseline']
    base={};step=rules['roundStep']
    for tpl,p in props.items():
        caliber=p.get('Caliber');profile=rules['calibers'].get(caliber)
        if caliber in rules['excludedCalibers']:continue
        assert profile is not None,('Unbalanced caliber',caliber)
        flesh=p['Damage']*max(1,p.get('buckshotBullets',0));flesh_factor=1+max(0,flesh/profile['fleshBaseline']-1)*rules['fleshWeight']
        pen_factor=next(b['multiplier'] for b in rules['penetrationBands'] if p['PenetrationPower']<=b['maximum'])
        amount=max(original[tpl]*rules['nativeMultiplier'],rules['legacyCashAnchors'].get(tpl,0)*rules['legacyCashMinimumMultiplier'],profile['floor']*max(flesh_factor,pen_factor))
        base[tpl]=int(math.ceil(amount/step)*step)
    before=copy.deepcopy(manifest['offers']);cash=collections.defaultdict(list)
    for offer in manifest['offers']:
        if offer['price']<=0 or offer['tpl'] not in base:continue
        price=int(math.floor(base[offer['tpl']]*rules['cashLevelMultipliers'][str(offer['level'])]/step+.5)*step)
        offer['price']=price;cash[offer['tpl']].append(price)
        trader['assort']['barter_scheme'][offer['id']][0][0]['count']=price
    reference={tpl:min(cash[tpl]) if cash[tpl] else value for tpl,value in base.items()}
    packaging={tpl:rules['packagingReference'] for tpl,t in items.items() if t.get('_parent')=='543be5cb4bdc2deb348b4568' and t.get('_props',{}).get('StackSlots') and tpl in original}
    packaging.update({tpl:rules['packagingReference'] for tpl in rules['additionalPackaging']})
    # Existing low-grade barter contents are intentional; use the matching native wrapper.
    roots=[i for i in trader['assort']['items'] if i.get('parentId')=='hideout'];corrections=[]
    for root in roots:
        children=[i for i in trader['assort']['items'] if i.get('parentId')==root['_id'] and i.get('slotId')=='cartridges']
        if root['_tpl'] not in packaging or not children:continue
        types={c['_tpl'] for c in children};assert len(types)==1
        ammo=next(iter(types));count=sum(c['upd']['StackObjectsCount'] for c in children);current=items[root['_tpl']]['_props']['StackSlots'][0]
        if current['_props']['filters'][0]['Filter']!=[ammo] or current['_max_count']!=count:
            matches=[tpl for tpl,t in items.items() if tpl in packaging and len(t['_props']['StackSlots'])==1 and t['_props']['StackSlots'][0]['_max_count']==count and t['_props']['StackSlots'][0]['_props']['filters'][0]['Filter']==[ammo]]
            assert matches,('No native wrapper matches barter contents',root['_id'],ammo,count)
            old=root['_tpl'];root['_tpl']=sorted(matches)[0];corrections.append(dict(offer=root['_id'],old=old,new=root['_tpl'],ammunition=ammo,rounds=count))
            for collection in [manifest['offers'],manifest['barters']]:
                for o in collection:
                    if o['id']==root['_id']:o['tpl']=root['_tpl']
            for q in quests.values():
                for reward in q['rewards']['Success']:
                    if reward['type']=='AssortmentUnlock' and reward.get('target')==root['_id']:
                        for i in reward.get('items',[]):
                            if i['_id']==root['_id']:i['_tpl']=root['_tpl']
        maximum=items[ammo]['_props']['StackMaxSize'];needed=math.ceil(count/maximum)
        assert needed>=len(children),('Surplus source cartridge entries',root['_id'])
        for index in range(needed):
            if index<len(children):child=children[index]
            else:
                child=copy.deepcopy(children[0]);child['_id']=hashlib.sha256(('ember.trade-pack.'+root['_id']+'.'+str(index)).encode()).hexdigest()[:24];trader['assort']['items'].append(child)
            child['location']=index;child['upd']['StackObjectsCount']=min(maximum,count-index*maximum)
        for collection in [manifest['offers'],manifest['barters']]:
            for offer in collection:
                if offer['id']==root['_id']:offer['name']=items[root['_tpl']]['_name']
    for oid,requirements in rules.get('barterInputs',{}).items():
        assert trader['assort']['barter_scheme'][oid][0][0]['_tpl']!='5449016a4bdc2d6f028b456f'
        trader['assort']['barter_scheme'][oid]=[[dict(_tpl=tpl,count=count) for tpl,count in requirements.items()]]
        for collection in [manifest['offers'],manifest['barters']]:
            for offer in collection:
                if offer['id']==oid:
                    # Keep the manifest's reviewable requirement list in sync with the native scheme.
                    offer['barter']=[dict(_tpl=tpl,count=count) for tpl,count in requirements.items()]
    for rid,rule in rules.get('craftOverrides',{}).items():
        recipe=next(r for r in recipes if r['_id']==rid)
        assert recipe['endProduct'] in base
        recipe['count']=rule['count'];recipe['productionTime']=rule['productionTime']
        prototype=copy.deepcopy(next(r for r in recipe['requirements'] if r['type']=='Item'))
        recipe['requirements']=[r for r in recipe['requirements'] if r['type']!='Item']
        for tpl,count in rule['consumed'].items():
            requirement=copy.deepcopy(prototype);requirement.update(templateId=tpl,count=count);recipe['requirements'].append(requirement)
        review=next(r for r in manifest['recipes'] if r['id']==rid)
        review['count']=rule['count'];review['minutes']=rule['productionTime']/60
        review['materials']=[dict(name='火药' if tpl=='5d6fc78386f77449d825f9dc' else '金属零件',count=count) for tpl,count in rule['consumed'].items()]
    config['economy']['handbookPrices'].update(reference);config['economy']['handbookPrices'].update(packaging)
    config['economy']['version']='2.2.0';config['economy']['ammunitionProfile']=rules['profile'];config['economy']['referenceMode']='cash-weapons-parts-and-ammunition'
    manifest['economy']['ammunitionProfile']=rules['profile']
    changed=[]
    old_byid={o['id']:o for o in before}
    for o in manifest['offers']:
        old=old_byid[o['id']]
        if o['price']!=old['price']:changed.append(dict(offer=o['id'],tpl=o['tpl'],level=o['level'],before=old['price'],after=o['price'],quota=o['limit']))
    early_before=sum(o['price']*o['limit'] for o in before if o['level']==1 and not o.get('unlock') and o['tpl'] in base)
    early_after=sum(o['price']*o['limit'] for o in manifest['offers'] if o['level']==1 and not o.get('unlock') and o['tpl'] in base)
    report=dict(profile=rules['profile'],ammunitionTemplates=len(reference),packagingTemplates=len(packaging),changedOffers=changed,wrapperCorrections=corrections,barterInputs=rules.get('barterInputs',{}),craftOverrides=rules.get('craftOverrides',{}),firstTierAllAmmunitionBefore=early_before,firstTierAllAmmunitionAfter=early_after,usesFixedPriceAnchors=True,playerProfilesRead=False)
    if a.write:
        for p,v in [(data/'trader.json',trader),(data/'manifest.json',manifest),(data/'quests.json',quests),(data/'recipes.json',recipes),(repo/'config/gameplay.json',config)]:write(p,v)
    a.report.parent.mkdir(parents=True,exist_ok=True);write(a.report,report);print(json.dumps(report,ensure_ascii=False,indent=2))

if __name__=='__main__':main()
