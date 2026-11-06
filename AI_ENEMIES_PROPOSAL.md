# AI Enemies Proposal

Nov 4, 2026 · @user

Milestone 1's AI should be two kinds of armed people, holdout looters and contractor patrols. They run only on the server, see and hear the world the way a player does, and move and shoot through exactly the same code as players. This builds on the AI section of the Unity plan (server-only, a brain that decides and a body that acts, the Patrol, Investigate, Attack, Search loop, bodies that become loot) and fills in what it left open.

## Already decided

| Question | Decision |
| --- | --- |
| How AI bodies move | The same movement code as players. The NavMesh plans the route; the brain steers the body with the same move and look input a player sends |
| Can AI factions fight each other | Yes. Looters and contractors are hostile to each other, so raids have fights that don't involve the player |
| Who builds what | A proposed split (Who builds what, below), for Will and John to agree |

Everything else here is a proposal. The biggest ask of John is the movement split: `PlayerController` becomes a motor that any input can drive, with player prediction as a wrapper around it.

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

## Shared movement

AI and players drive the same character motor, so an AI walks, sprints, climbs slopes and collides exactly like a player. Only where the input comes from differs.

&#91;embedded content: movement · two input sources, one motor\]

**The split.** `PlayerController` becomes two parts:

- **`CharacterMotor`**: takes one input (move, look, jump, sprint) and a time step, and moves the `CharacterController` with today's walk, run, jump, gravity and friction rules. It holds the state that prediction needs (velocity, grounded, fall speed) and can save and restore it. No networking in it.
- **`PlayerController`**: reads the owner's input and keeps today's `[Replicate]` and `[Reconcile]`, calling the motor inside the replicate. Reconcile saves and restores the motor's state.

**The AI body.** On the server only, `AIBody` asks the NavMesh for a path to wherever the brain wants to go, turns the next corner into move input, and turns the brain's aim point into look input. It calls the motor on Fish-Net's tick, the same rate players move at. Clients see a networked character and interpolate it; AI need no prediction because nobody's input drives them remotely.

**Rules that keep paths walkable:**

- The NavMesh agent's radius, height, step height and slope limit match the `CharacterController`'s, so every path the NavMesh gives is one the motor can walk.
- If an AI makes less than about a metre of progress in two seconds, it asks for a new path; after three tries the brain picks another goal.
- Doors and ladders wait until the map has them.

**Why now.** `PlayerController` is about 300 lines today. Splitting it now is a small change; once weapons, leaning and stances land in it, it's a much bigger one. It's John's code, so the timing is his call.

## The brain

A hand-written state machine per AI, ticking a few times a second on the server. No behaviour trees or GOAP: four or five states don't need the machinery, and the free tools for them are Asset Store packages the public repo can't use.

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

**Hearing.** Gunshots raise a noise with a radius; AI inside it get the rough position, not the exact one. Footsteps come later, together with the player's own audio work.

**The fog and the dark apply to AI too.** An AI in the fog is as blind as a player in it, and it doesn't know what's in it either. Night vision and thermal, when they exist, change an AI's sight the same way they change a player's.

**Accuracy.** AI fire through the same weapon and damage code as players, from their eyes along their aim:

- A reaction delay between full awareness and the first shot (the plan already asks for this).
- Spread starts wide and tightens the longer the AI tracks the same target; it widens again when the target moves fast or the AI is hit.
- Spread grows with distance, per the AI's profile.
- No aiming at the head on purpose: AI aim at the body, and head hits come from spread.
- An AI never shoots at what it can't see. Foliage that blocks its sight blocks its shot, even if the bullet could pass.

## Cover, spawning, gear and death

AI use cover the level designer marks, spawn where the map brief says, carry real items rolled from the loot tables, and leave them on their body.

**Cover.** For the greybox, hand-placed `CoverPoint` markers: a position, the direction it protects from, and whether it's low (crouch) or high (stand). The brain picks cover that blocks the threat's line of sight and is closer to the AI than to the threat. An editor tool that finds cover from the NavMesh and geometry can come once the maps are real.

**Spawning.** Each map has `AISpawnZone`s: a faction, a profile, how many (a range), and for contractors a patrol route. The server rolls them at raid start, from the raid's loot seed so a raid can be replayed. No AI spawns where a player can see it. More AI arriving mid-raid waits for a later milestone.

**Gear.** Each AI gets a real raid inventory rolled from a loot table named in its profile, through the same code that fills crates (`LootTableAsset`). The gun it shoots you with is the gun on its body. For Milestone 1 its reserve ammo is unlimited but reloads take the real time, and its body keeps a partly full magazine.

**Death.** Health at zero ends the brain. The body stays where it fell as a loot container, using the existing container code, with everything it carried. Killing an AI is a raid outcome like any other: what you take from it comes home only if you extract.

**The profile.** `AIProfile` (a ScriptableObject), extending the plan's list:

| Field | What it sets |
| --- | --- |
| Faction | Who it's hostile to, via the table above |
| Health | As players |
| Loot table | What it carries, including its weapon |
| Sight | View distance, view angle, how fast awareness fills |
| Reaction time | Delay before the first shot |
| Accuracy | Spread by distance, how fast it tightens |
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

| Part | Who | Needs first |
| --- | --- | --- |
| `CharacterMotor` split out of `PlayerController` | John | Nothing |
| Weapons and damage, through one path for players and AI | John | Nothing (already his step 4) |
| `AIBody`: path following and aim on the motor | John | The motor |
| Factions table, `AIProfile`, spawn zones, loot on the body | Will | Nothing |
| Perception: sight, hearing, awareness | Will | Nothing; tested without Unity |
| The brain: states, Attack scoring, squad memory | Will | Perception |
| Debug view and the CI AI test | Will | Body and brain |

**Order, with a check at each step:**

1. **Motor split.** Check: a player moves exactly as before, at 100 ms simulated latency.
2. **An AI body that walks.** A debug key sends it to a point. Check: it reaches any point on the greybox.
3. **Perception and the debug view.** Check: the overlay shows awareness rising as you walk into view, and falling in the dark.
4. **Patrol, Investigate, Search.** No shooting yet. Check: it notices you, comes to look, loses you, searches and gives up.
5. **Attack**, once weapons land. Check: the plan's AI done list, and its shots miss more at range and against a moving target.
6. **Two factions, squads and loot.** Check: looters and contractors fight each other unprompted; killing one leaves a lootable body with the gun it used.

## Open questions

- [ ] Does John want the motor split before weapons, or right after? Before is smaller; after keeps his current work moving.
- [ ] What do AI do when the fog comes? Proposal for Milestone 1: they ignore it, and any AI it reaches is gone, like a player who's Missing in Action. Moving uphill ahead of it, toward the players' last extracts, is a later idea.
- [ ] How many AI per raid on the greybox? Proposal: start with two looter pairs and one contractor fireteam, and tune from playtests.
- [ ] How good is contractor loot? They're the setting's source of high-tier gear, so a body worth the risk, but a starter map can't hand out plate carriers every raid.
- [ ] Footsteps: AI hearing them waits for the player's footstep audio, so both use one set of rules. Agree?
- [ ] Picking up and carrying loot, and extracting, stay out of Milestone 1. They're what makes player-like bots, which this design leaves room for.
