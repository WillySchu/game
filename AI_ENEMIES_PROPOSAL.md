# AI Enemies Proposal

Nov 4, 2026 · @user

Milestone 1's AI should be two kinds of armed people, holdout looters and contractor patrols. They run only on the server, see and hear the world the way a player does, and move and shoot through exactly the same code as players. This builds on the AI section of the Unity plan (server-only, a brain that decides and a body that acts, the Patrol, Investigate, Attack, Search loop, bodies that become loot) and fills in what it left open.

## Already decided

| Question | Decision |
| --- | --- |
| How AI bodies move | The same movement code as players. The NavMesh plans the route; the brain steers the body with the same move and look input a player sends |
| Where shots come from | The muzzle, for players and AI alike: the ray starts at the gun's muzzle and goes along its aim, with the gun's spread and recoil |
| Can AI factions fight each other | Yes. Looters and contractors are hostile to each other, so raids have fights that don't involve the player |
| Who builds what | A proposed split (Who builds what, below), for Will and John to agree |

Everything else here is a proposal. The biggest ask of John is the split: `PlayerController` becomes a motor and a gun that any input can drive, with player prediction as a wrapper around them.

## Who the AI are

Two kinds of AI for Milestone 1, taken from the Cascadia Setting and the Hood Canal Map Brief. They differ in how they fight, not just in what they carry.

|  | Holdout looters | Contractor patrols |
| --- | --- | --- |
| Who they are | Armed locals picking over the zone; the scav equivalent | The cordon's private security contractor |
| Group | Alone or in loose pairs | Pairs or fireteams of three or four |
| Gear | Civilian: hunting rifles, shotguns, lever guns, handguns | Patrol and military rifles, armour |
| How they fight | Opportunistic: push when they have the numbers, break off when hurt, poor teamwork | Disciplined: hold cover, flank, call each other in |
| Movement | Wander between loot spots | Walk set routes between posts |
| Where (Hood Canal) | Town along 101, the bridge approaches, the homestead | The navy shore hut, sometimes along 101. Nothing on the delta |

**Who's hostile to whom** lives in one table, so neutral holdouts and traders later are a data change, not new code:

|  | Players | Looters | Contractors |
| --- | --- | --- | --- |
| Looters | Hostile | Friendly | Hostile |
| Contractors | Hostile | Hostile | Friendly |

The setting doc's open tone question stays open: ordinary holdouts are neutral once traders exist; for now every holdout the player meets is an armed looter.

## Shared movement and shooting

AI and players drive the same character motor and the same gun, so an AI walks, sprints, climbs slopes, collides and shoots exactly like a player. Only where the input comes from differs.

&#91;embedded content: movement · two input sources, one motor\]

**The split.** `PlayerController` becomes three parts:

- **`CharacterMotor`**: takes one input (move, look, jump, sprint) and a time step, and moves the `CharacterController` with today's walk, run, jump, gravity and friction rules. It holds the state that prediction needs (velocity, grounded, fall speed) and can save and restore it. No networking in it.
- **The gun** (a weapon component beside the motor): takes "trigger held, reload pressed, switch fire mode" and a time step, and does what the player's replicate does today: fire modes and rate of fire, the loaded-round count, the reload state, the shot from the muzzle with the gun's spread (its accuracy, from the synced random numbers), recoil kicking the aim, the tracer, and, on the server, `FireRound` and the hit. It holds the state prediction needs (rounds, fire mode, time since the last shot, reload progress) and can save and restore it. No networking in it beyond asking the server's raid code.
- **`PlayerController`**: reads the owner's input and keeps today's `[Replicate]` and `[Reconcile]`, calling the motor and the gun inside the replicate. Reconcile saves and restores both.

**The AI body.** On the server only, `AIBody` asks the NavMesh for a path to wherever the brain wants to go, turns the next corner into move input, turns the brain's aim point into look input, and holds the trigger or presses reload when the brain says. It calls the motor and the gun on Fish-Net's tick, the same rate players move at. Clients see a networked character and interpolate it; AI need no prediction because nobody's input drives them remotely. Other players already see a player's shots through a message from the server (the shooter's own client draws its own), and an AI's shots go out the same way.

**Rules that keep paths walkable:**

- The NavMesh agent's radius, height, step height and slope limit match the `CharacterController`'s, so every path the NavMesh gives is one the motor can walk.
- If an AI makes less than about a metre of progress in two seconds, it asks for a new path; after three tries the brain picks another goal.
- Doors and ladders wait until the map has them.

**Why now.** This proposal warned that the split grows as weapons, leaning and stances land in `PlayerController`, and weapons have: it's over 700 lines now, with movement and firing interleaved in one replicate. It's still a contained change (two components and the reconcile data to save and restore), but each new feature makes it bigger, and AI can't move or shoot until it's done. It's John's code, so the timing is his call.

## The brain

A hand-written state machine per AI, ticking a few times a second on the server. No behaviour trees or GOAP for now: four or five states don't need the machinery, and a state machine in plain C# can be unit-tested and reviewed like the rest of the code. If the AI outgrows it (many enemy types, or a long list of interruptions), Unity's own Behavior package (free and first-party, so fine for the public repo) is the one to move to; perception and the body wouldn't change.

&#91;embedded content: AI states · 5 states, what moves an AI between them\]

**States.** The plan's four, plus Retreat:

- **Patrol**: contractors walk their route; looters wander between loot spots and stop at each for a while.
- **Investigate**: go to a sound or a glimpse, carefully, and look around.
- **Attack**: fight a target it can see or has just seen (below).
- **Search**: go to where the target was last seen, then check the nearby cover, then give up.
- **Retreat** (looters, and anyone out of ammo): break contact, get to cover away from the threat, heal or reload, then Search or Patrol.

**Inside Attack, a choice about once a second.** The AI scores four actions and takes the best: push closer, hold its cover and shoot, move to better cover, or reload. Each AI's profile weights them, which is most of what makes the two factions feel different. Looters weight pushing high while they outnumber you and retreating once hurt; contractors weight holding and flanking.

**Squad memory.** AI spawned together share what they know: the last place a target was seen or heard, and when. One looter spotting you sends his partner to investigate; a contractor under fire calls the fireteam, and one of them takes the flank. This is the cheapest way to make AI feel smart, because coordination is what players notice.

**Faction fights.** The brain treats any hostile in the faction table as a target, AI included, through the same perception. Nothing special is needed for looters and contractors to fight.

## Perception and fairness

AI only know what they've seen or heard, and they see and hear under the same rules as players. The most common complaint about AI in this genre is instant, perfect shots through cover nobody could see through; these rules design that out.

**An awareness meter, not a yes or no.** Each AI keeps a 0 to 1 awareness of each target it can see. It fills while the target is in view and drains when it isn't:

| Fills faster | Fills slower |
| --- | --- |
| Close | Far |
| Moving, sprinting | Still, crouched |
| Lit, on open ground | Dark, rain, foliage, fog |
| In the middle of the view cone | At its edge |

Past about a third, the AI turns to look (Investigate). Full, it's in Attack. Sight is checked 5 to 10 times a second by casting lines to the target's head and chest, never every frame.

**Hearing.** Gunshots raise a noise with a radius; AI inside it get the rough position, not the exact one. Every real shot already passes through one place on the server (where it's fired, and where the server tells other players to draw it), which is where the noise is raised, so AI hear players' shots and each other's alike. Footsteps come later, together with the player's own audio work.

**The fog and the dark apply to AI too.** An AI in the fog is as blind as a player in it, and it doesn't know what's in it either. Night vision and thermal, when they exist, change an AI's sight the same way they change a player's.

**Accuracy.** AI fire through the same gun and damage code as players, from the muzzle along their aim. The gun's own spread and recoil apply to them as to players; an AI's aiming error is added on top, not instead:

- A reaction delay between full awareness and the first shot (the plan already asks for this).
- Aiming error starts wide and tightens the longer the AI tracks the same target; it widens again when the target moves fast or the AI is hit.
- Aiming error grows with distance, per the AI's profile.
- Recoil kicks an AI's aim as it does a player's, and the AI has to bring it back on target, which spaces out its shots without any special rule.
- No aiming at the head on purpose: AI aim at the body, and head hits come from spread.
- An AI never shoots at what it can't see. Foliage that blocks its sight blocks its shot, even if the bullet could pass.
- An AI checks its line of fire from the muzzle, not its eyes, before it shoots: a target it can see over a low wall may still be behind the wall for its gun.

**Hits.** Damage is already in: `RaidServer.Hit` and `DamageRules` (100 health; head ×3, torso ×1, limbs ×0.6; body armor covers the torso and wears). An AI body takes hits the same way the player does, through `IDamageable`, with the body part from its hitboxes once players have them. Contractors' armour works through the same rules with no AI-specific code. Shots ignore trigger boxes, so loot on the ground and bodies don't block them.

**Shooting moving AI.** AI fire on the server, with no lag, so their shots land where they aim. Players see AI smoothed and slightly in the past, and the server checks hits against where everything is now, with no lag compensation (Fish-Net's collider rollback is a Pro feature). So a moving AI is harder to hit than it looks. For Milestone 1 that's acceptable, but a short history of where everyone was over the last few ticks, used to check each shot against what the shooter saw, would help against moving AI and moving players alike. Open question below.

## Cover, spawning, gear and death

AI use cover the level designer marks, spawn where the map brief says, carry real items rolled from the loot tables, and leave them on their body.

**Cover.** For the greybox, hand-placed `CoverPoint` markers: a position, the direction it protects from, and whether it's low (crouch) or high (stand). The brain picks cover that blocks the threat's line of sight and is closer to the AI than to the threat. An editor tool that finds cover from the NavMesh and geometry can come once the maps are real.

**Spawning.** Each map has `AISpawnZone`s: a faction, a profile, how many (a range), and for contractors a patrol route. The server rolls them at raid start, from the raid's loot seed so a raid can be replayed. No AI spawns where a player can see it. More AI arriving mid-raid waits for a later milestone.

**Gear.** Each AI gets a real raid inventory rolled from a loot table named in its profile, through the same code that fills crates (`LootTableAsset`). The gun it shoots you with is the gun on its body. It fires real rounds (`FireRound`) and reloads the way players do (`Reload`: the fullest magazine from its rig, then its backpack, taking the gun's reload time once that's used), so it carries real spare magazines. For Milestone 1 its reserve is effectively unlimited: the server tops its spare magazines up, or its loot table is generous. Its body keeps whatever it really had, partly full magazine included.

**AI in the raid's server code.** The raid server's calls for firing, reloading and hits (`FireRound`, `Reload`, `LoadedRounds`, `Hit`) take a player's network connection today, and an AI has none. They become calls by ID, with each player's connection looking up their ID and each AI getting one of its own. Most of what's behind them is keyed by ID already: the raid inventory and the health tracking (`RaidHealth`). AI aren't in the backend's raid records, though: nothing about them is reported home.

**Death.** Health at zero ends the brain. The body stays where it fell as a loot container, using the existing body code, with everything it carried. A player's death (`PlayerDied`) also reports to the backend and sends them home; an AI's only needs the body and the brain switched off. Killing an AI is a raid outcome like any other: what you take from it comes home only if you extract.

**The profile.** `AIProfile` (a ScriptableObject), extending the plan's list:

| Field | What it sets |
| --- | --- |
| Faction | Who it's hostile to, via the table above |
| Health | As players (100, `DamageRules.MaxHealth`) |
| Loot table | What it carries, including its weapon |
| Sight | View distance, view angle, how fast awareness fills |
| Reaction time | Delay before the first shot |
| Accuracy | Aiming error by distance, how fast it tightens (on top of the gun's own spread) |
| Attack weights | Push, hold, reposition, reload |
| Courage | How hurt it gets before it retreats |

## Running cost, debugging and tests

The plan's target stands: 8 to 10 AI on a map without a noticeable cost to the server's frame time. Three habits get there, and a debug view makes them tunable.

**Cost.**

- Brains and sight checks run 5 to 10 times a second, spread across frames so they don't all land on one.
- Paths are asked for when the goal changes or the AI gets stuck, not every tick.
- Fish-Net's distance-based visibility stops sending AI to clients far from them, which saves bandwidth as maps grow.

**The debug view.** In the editor and in Development builds, a toggle (on the F1 overlay) draws over each AI: its state, its awareness of each target, its last known position for its target, its path and its chosen cover. AI can't be tuned without seeing what they think.

**Tests.**

- Perception, the Attack scoring and squad memory are plain C# with no Unity in them, like the inventory rules in `shared/`, so they get unit tests in the harness.
- The CI raid test gets a third run: one AI placed near the scripted player, which must notice, fire and kill it, and the result must come back as died.

## Who builds what

Proposed: John builds what touches the player's character and weapons; Will builds what runs only on the server. Neither waits long on the other.

Already done: firing, fire modes, ammo and reloads (John, with the server side by Will), and damage, health, armor and death on the server (Will).

| Part | Who | Needs first |
| --- | --- | --- |
| `CharacterMotor` and the gun split out of `PlayerController` | John | Nothing |
| Hitboxes on the player (and so on the AI body) | John | Nothing |
| `AIBody`: path following, aim and trigger on the motor and the gun | John | The split |
| Raid server calls by ID, so AI can fire, reload and be hit; AI death and bodies | Will | Nothing |
| Factions table, `AIProfile`, spawn zones, loot on the body | Will | Nothing |
| Perception: sight, hearing, awareness | Will | Nothing; tested without Unity |
| The brain: states, Attack scoring, squad memory | Will | Perception |
| Debug view and the CI AI test | Will | Body and brain |

**Order, with a check at each step:**

1. **Motor and gun split.** Check: a player moves and shoots exactly as before, at 100 ms simulated latency.
2. **An AI body that walks.** A debug key sends it to a point. Check: it reaches any point on the greybox.
3. **Perception and the debug view.** Check: the overlay shows awareness rising as you walk into view, and falling in the dark.
4. **Patrol, Investigate, Search.** No shooting yet. Check: it notices you, comes to look, loses you, searches and gives up.
5. **Attack.** Weapons and damage have landed; hitboxes make hits more precise but don't block this, as AI can already hit a player's capsule. Check: the plan's AI done list, and its shots miss more at range and against a moving target.
6. **Two factions, squads and loot.** Check: looters and contractors fight each other unprompted; killing one leaves a lootable body with the gun it used.

## Open questions

- [ ] When does John split the motor and the gun out of `PlayerController`: before the AI work starts, or alongside it? Before means the AI body has something to drive from day one.
- [ ] Shooting moving AI (and players): accept checking hits against where things are now for Milestone 1, or build the short position history as part of the AI work?
- [ ] What do AI do when the fog comes? Proposal for Milestone 1: they ignore it, and any AI it reaches is gone, like a player who's Missing in Action. Moving uphill ahead of it, toward the players' last extracts, is a later idea.
- [ ] How many AI per raid on the greybox? Proposal: start with two looter pairs and one contractor fireteam, and tune from playtests.
- [ ] How good is contractor loot? They're the setting's source of high-tier gear, so a body worth the risk, but a starter map can't hand out plate carriers every raid.
- [ ] Footsteps: AI hearing them waits for the player's footstep audio, so both use one set of rules. Agree?
- [ ] Picking up and carrying loot, and extracting, stay out of Milestone 1. They're what makes player-like bots, which this design leaves room for.
