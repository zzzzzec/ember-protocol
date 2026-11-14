param([Parameter(Mandatory=$true)][string]$GameRoot,[Parameter(Mandatory=$true)][string]$Report)
$ErrorActionPreference='Stop'
$null=[Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'SPT\Mono.Cecil.dll'))
$module=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $GameRoot 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))
$methods=@(@('BotsPresets','method_2',2),@('BotsPresets','.ctor',5),@('BotsPresets','TryLoadBotsProfilesOnStart',1),@('TarkovApplication','method_41',2),@('MetricsEventsClass','SetMatchingCompleted',0),@('MetricsEventsClass','SetLocationLoaded',0),@('MetricsEventsClass','SetGamePooled',0),@('BossSpawnScenario','Run',2),@('BossSpawnerClass','method_3',6),@('BossSpawnerClass','method_0',3),@('BotSpawner','Stop',0),@('PlaceItemTrigger','TriggerEnter',1),@('PlaceItemTrigger','TriggerExit',1),@('TriggerWithId','get_Id',0),@('QuestsListView','Show',5),@('BotHearingSensor','method_0',4),@('BotHalloweenWithZombiesGameEvent','Call',3),@('TraderPanel','Update',0),@('TraderCard','LateUpdate',0),@('TraderCard','Show',3),@('TraderCard','method_0',0),@('BotMemoryClass','set_GoalEnemy',1),@('BotMemoryClass','method_0',0),@('BotHalloweenWithZombies','method_8',2),@('BotHalloweenWithZombiesGameEvent','get_FindPointCenterPosition',0),@('BotHalloweenWithZombiesGameEvent','get_HoldPeriod',0),@('BotHalloweenWithZombiesGameEvent','TryRecalcPatrolParams',0),@('GClass99','FindPoint',3))
$fields=@(@('QuestsListView','_questsCounterText'),@('BotHearingSensor','BotOwner'),@('BotHalloweenWithZombiesGameEvent','BotOwner_0'),@('TraderPanel','traderInfo_0'),@('TraderPanel','_timeLeft'),@('TraderPanel','_timeGroup'),@('TraderPanel','_restockedGroup'),@('TraderCard','traderInfo_0'),@('TraderCard','_timeLeft'),@('TraderCard','_standing'),@('BotMemoryClass','BotOwner_0'),@('GClass99','BotOwner_0'),@('GClass99','CustomNavigationPoint_0'),@('BotHalloweenWithZombiesGameEvent','CachedLeader'),@('BotHalloweenWithZombiesGameEvent','FindPointCenterPosition_1'),@('BotHalloweenWithZombiesGameEvent','HoldPeriod_1'),@('BotHalloweenWithZombiesGameEvent','NextChangePositionTime'))
$methods+=,@('BotCreatorClass','.ctor',3)
$methods+=,@('BotWeaponManager','PreActivate',0)
$methods+=,@('PoolManagerClass','CreateItem',4)
$methods+=,@('Player','method_137',1)
$methods+=@(@('GClass224','NotMovingCheck',0),@('EnemyInfo','SetVisible',1),@('BotsGroup','ReportAboutEnemy',3),@('Player','Rotate',2),@('MovementContext','ApplyRotation',1),@('BotMemoryClass','SetCoverPoints',2),@('BotMemoryClass','CheckIsInCover2',0),@('BaseBrain','CalcActionNextFrame',0))
$methods+=@(@('BotSteering','SetXAngle',1),@('GClass498','SetXAngle',1))
$fields+=@(@('GClass224','BotOwner_0'),@('GClass224','Float_2'),@('GClass224','Int_0'),@('MovementContext','_player'))
$methods+=,@('LocationInfoPanel','Set',9)
$fields+=@(@('LocationInfoPanel','location_0'),@('LocationInfoPanel','_description'))
$fields+=,@('GClass429','BotOwner_0')
$methods+=@(@('BotOwner','UpdateManual',0),@('BotOwner','FixedUpdate',0),@('BotOwner','method_9',3),@('AICoreAgentClass','Update',0))
$fields+=,@('BotCreatorClass','Func_0')
try {
 foreach($check in $methods){
  $type=$module.Types|Where-Object Name -eq $check[0]
  if(@($type.Methods|Where-Object {$_.Name -eq $check[1] -and $_.Parameters.Count -eq $check[2]}).Count -ne 1){throw ('Native method binding mismatch: '+$check[0]+'.'+$check[1])}
 }
 $countdown=$module.Types|Where-Object FullName -eq 'EFT.UI.Matchmaker.MatchmakerFinalCountdown'
 if(@($countdown.Methods|Where-Object {$_.Name -eq 'Show' -and $_.Parameters.Count -eq 2 -and $_.Parameters[0].ParameterType.Name -eq 'Profile' -and $_.Parameters[1].ParameterType.FullName -eq 'System.DateTime'}).Count -ne 1){throw 'Countdown start binding mismatch.'}
 $spawner=$module.Types|Where-Object FullName -eq 'EFT.BotSpawner'
 if(@($spawner.Methods|Where-Object {$_.Name -eq 'ActivateBotsByWave' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.Name -eq 'BossLocationSpawn'}).Count -ne 1){throw 'Finite wave dispatcher binding mismatch.'}
 $armorBindings=@(@('EFT.Player','SetShotStatus',5),@('EFT.Player','ApplyShot',5),@('EFT.InventoryLogic.ArmorComponent','ShotMatches',2),@('EFT.InventoryLogic.Inventory','GetPutOnArmorsNonAlloc',1),@('EFT.MovementContext','get_PlayerAnimatorDeltaPosition',0))
 foreach($check in $armorBindings){
  $type=$module.Types|Where-Object FullName -eq $check[0]
  if(@($type.Methods|Where-Object {$_.Name -eq $check[1] -and $_.Parameters.Count -eq $check[2]}).Count -ne 1){throw ('Native threat binding mismatch: '+$check[0]+'.'+$check[1])}
 }
 $profile=$module.GetType('EFT.Profile')
 if(@($profile.Methods|Where-Object {$_.IsConstructor -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.Name -eq 'CompleteProfileDescriptorClass'}).Count -ne 1){throw 'PMC skill constructor binding mismatch.'}
 $movement=$module.GetType('EFT.MovementContext')
 if(@($movement.Fields|Where-Object {$_.Name -eq '_player' -and $_.FieldType.FullName -eq 'EFT.Player'}).Count -ne 1){throw 'Player sprint context field binding mismatch.'}
 $sprintAcceleration=$movement.Methods|Where-Object Name -eq 'SprintAcceleration'
 if(-not ($sprintAcceleration.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_SprintingSpeed' })){throw 'Native sprint movement no longer consumes the speed curve.'}
 $playerType=$module.GetType('EFT.Player')
 $firearm=$playerType.NestedTypes|Where-Object Name -eq 'FirearmController'
 $clientFirearm=$module.GetType('EFT.ClientFirearmController')
 $initiateShot=$firearm.Methods|Where-Object Name -eq 'InitiateShot'
 if(@($initiateShot.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'OnMakingShot' }).Count -ne 1){throw 'Live round practice binding mismatch.'}
 foreach($name in @('ReloadMag','QuickReloadMag','ReloadWithAmmo','ReloadCylinderMagazine','ReloadBarrels')){
  foreach($controller in @($firearm,$clientFirearm)){
   if(@($controller.Methods|Where-Object { $_.Name -eq $name -and @($_.Parameters|Where-Object { $_.Name -eq 'callback' -and $_.ParameterType.FullName -eq 'Comfort.Common.Callback' }).Count -eq 1 }).Count -ne 1){throw ('Reload completion callback binding mismatch: '+$name)}
  }
 }
 $quick=$clientFirearm.Methods|Where-Object Name -eq 'QuickReloadMag'
 if(-not ($quick.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'QuickReloadMag' -and $_.Operand.DeclaringType.FullName -eq $firearm.FullName })){throw 'Native client quick reload no longer calls the patched base method.'}
 if(@($playerType.Events|Where-Object Name -eq 'OnPlayerDeadStatic').Count -ne 1){throw 'Native armor practice death event mismatch.'}
 $speech=$module.GetType('EFT.Player').Methods|Where-Object Name -eq 'PlaySpeechFromTime'
 $speechGain=@($speech.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldfld' -and $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.DeclaringType.Name -eq 'TaggedClip' -and $_.Operand.Name -eq 'Volume'})
 if($speechGain.Count -ne 1 -or -not ($speech.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'BetterSource' -and $_.Operand.Name -eq 'Play'})){throw 'Infected speech gain binding mismatch.'}
 $crowd=$module.GetType('GClass1196').Methods|Where-Object Name -eq 'method_4'
 $crowdGain=@($crowd.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq 1})
 if($crowdGain.Count -ne 1 -or -not ($crowd.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'BetterAudio' -and $_.Operand.Name -eq 'PlayAtPointDelayed'})){throw 'Infected crowd gain binding mismatch.'}
 $lootActions=$module.GetType('GetActionsClass').Methods|Where-Object {$_.Name -eq 'smethod_8' -and $_.Parameters.Count -eq 2}
 if(-not ($lootActions.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MemberReference] -and $_.Operand.Name -match '^(get_)?IsZombieCorpse$' })){throw 'Native corpse actions no longer check the infected marker.'}
 $zombieMove=$module.Types|Where-Object Name -eq 'MoveZombieStateClass'
 $updatePosition=$zombieMove.Methods|Where-Object Name -eq 'UpdatePosition'
 if(-not ($updatePosition.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_PlayerAnimatorDeltaPosition' })){throw 'Zombie locomotion no longer consumes native root motion.'}
 foreach($check in $fields){
  $type=$module.Types|Where-Object Name -eq $check[0];$found=$false
  while($null -ne $type){
   if(@($type.Fields|Where-Object Name -eq $check[1]).Count -eq 1){$found=$true;break}
   if($null -eq $type.BaseType){break};$type=$type.BaseType.Resolve()
  }
  if(-not $found){throw ('Native field binding mismatch: '+$check[0]+'.'+$check[1])}
 }
 $patrol=$module.Types|Where-Object Name -eq 'GClass99'
 $find=$patrol.Methods|Where-Object Name -eq 'FindPoint'
 if(-not ($find.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_FindPointCenterPosition' })){throw 'Native infected patrol no longer consumes the overridden patrol centre.'}
 if(-not ($find.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetFreeClosePoint' })){throw 'Native infected patrol no longer selects a free navigation point.'}
 $parent=Split-Path -Parent $Report;if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
 [ordered]@{passed=$true;methods=$methods.Count;fields=$fields.Count;nativePatrolConsumesPersonalCentre=$true;nativePatrolKeepsFreeNavigationPointSelection=$true;liveGamePatchAcceptance=$false}|ConvertTo-Json|Set-Content -LiteralPath $Report -Encoding UTF8
 Write-Output 'PASS native client method and field bindings; live game patch acceptance remains separate.'
}finally{$module.Dispose()}
