# Companions

English · [Русский](companions.md)

The mod includes Agnes Tachyon, Matikanefukukitaru, Narita Taishin, Nice Nature, Sweep Tosho, Oguri Cap, Special Week, and Silence Suzuka. Each companion’s dialogue is in her folder under `models/`.

## Nice Nature

### Character

Nice Nature thinks of herself as a supporting character. She’ll be the first to joke about finishing third. Saying “I want to win” out loud is harder. Someone might count on her to win, and she’d have to look them in the eye after the race.

She’s on surer ground at home. She grew up in a neighborhood where people know and love her. She cooks, does the laundry, and picks up chores whenever she has a moment. Looking after someone comes easily when they’re working side by side. She only gets embarrassed when they notice who did the work. [Official Cygames profile](https://umamusume.jp/character/nicenature)

### Writing her in Jalopy

Let her make fun of herself when she actually feels awkward. In an ordinary conversation she can just ask you to pass her a rag. If you thank her, let her show for a moment that she’s pleased before she changes the subject back to the road. Save the bronze-medal joke for a conversation about her ambitions.

### Morning shopping at the motel

While you sleep at a town motel, Nature picks up missing supplies. She checks the trunk, the roof rack if you have one, and anything you’re holding. A partly used repair kit or an installed tool rack is enough. She only buys oil when every bottle is empty.

In one morning she can buy a spare repair kit, two-stroke oil, and gasoline. If a fuel can is empty or partly full, she tops it up. If there are several, she picks the one with the least fuel. If they’re all full, she skips the pump. She only buys and fills a new can if you don’t own one. The pump dispenses up to nine liters at one unit of currency per liter. If money is tight, she can buy a partial fill.

First she sets aside the cost of a room in the next town on your route, including the price increase the game will apply when you arrive. Your home in Germany is the only free place to sleep. On the way back she budgets for every motel, including Dresden. She spends what’s left at local prices, checking that you can afford the whole purchase and fit it in the trunk. The supplies are already there when you wake up. After saying good morning, she walks through the lobby to the passenger door, taking the stairs if your room is upstairs. Once you’re behind the wheel, she tells you what she bought and how much it cost.

**Testing in a debug build.** Select Nice Nature, enable DebugPanel, and press Ctrl+F4. Under **Morning shopping**, **Inspect supplies / prices** shows what she’ll buy and the cost. **Buy morning supplies** makes the purchase with in-game money. **Purchase receipt** shows the result. To test the normal morning sequence, use **To the motel now** and **Sleep in the motel bed now**.

For example, if the repair kit costs 15, oil costs 5, and a fuel can costs 10 plus 9 to fill it, she might say, “I picked up a repair kit, some oil, and a can of gas. It’s all in the trunk. Came to 39.”

### Scene idea: “While you were filling up”

While you refuel the Laika, Nature notices the dirty windshield and wipes the driver’s side. Her own half is still dirty when you get back behind the wheel. You can see who she thought of first.

If you notice, she says, “Can you see better? Good. I was starting to read every road sign out loud for you.”

### Four more ideas for the trip

**“We finally passed it.”** After the same car overtakes the Laika a few times, Nature starts recognizing it from a distance. The next time you pass it, a quiet “Yes!” slips out. A moment later she glances over. “You were counting too? I thought it was just me.”

**“You usually go for this one.”** After you choose the same radio station a few times, Nature remembers it. One morning while you fiddle with the ignition, she tunes the radio herself. She starts singing along to the first few bars and gives away how much she likes the station too.

**“This crate’s mine.”** If you’ve carried all the cargo yourself, Nature waits by the trunk at the next stop. “Let me pick which one you carry today.” She nudges the lighter crate toward you and takes the other in both hands. Only then does she ask, “Could you hold the door for me?”

**“A postcard for Mom.”** At the motel, Nature writes home. If you asked for her help that day, a detail from the trip finds its way between the lines about weather and prices. “He asked me to pick the route today. We made it before dark.” You can read the card if she holds it out and asks, “Does the stamp look straight to you?”

## Oguri Cap

### Character

Oguri recognizes people from her hometown by their voices in the stands. What they see at the finish matters to her. She wants to show them how far she’s come since leaving Kasamatsu. When fans started asking for autographs, she got out a handwriting workbook to practice her signature. When a letter arrives from home, it takes her a while to answer. She has so much to tell them that she hardly knows where to start. Sources in Uma Musume Global’s `master.mdb` include `text_data` for character 1006, categories 163 and 166, and `character_system_text` lines 10027, 91002, and 91006. [Cygames profile](https://umamusume.jp/character/oguricap)

Food often reminds her of a particular person. She remembers Tama teaching her to freeze juice. When she invites everyone to see the cherry blossoms, her first thought is the food. It tastes better together. She remembers to mention the flowers when someone asks. That’s how she lands a joke with a perfectly straight face. On the road she can go quiet for a long time before asking one precise question. When the driver needs a tip, she gives it plainly. See lines 91012, 91013, and 91016 for the same character.

### Why she gets in the Laika

In the mod’s story, there was always a meal waiting for Oguri after a race in Kasamatsu. Next time, she wants to cook for the people who cheered her on. Everyone at home already knows all her dishes. When she hears how many towns the driver will pass through, she asks to come along. She wants to bring back one new dish she can cook for everyone in a single big pot.

At the garage she offers to help with the car in exchange for a seat. On the road she takes notes on what makes a good meal after a long day. In Istanbul she decides to make lentil soup at home and invites the driver to join everyone at the table. If you turn back early, she settles on a dish she can make with ingredients already at home. This story unfolds in conversation during the trip.

### A snack from the trunk

Put a loose pack of sausages in the Laika’s trunk. After a minute of driving, stop and look over at the passenger seat. Oguri lifts a sausage to her mouth, chews, and only then admits what she’s done. One pack disappears from the trunk. She needs at least another minute on the road before her next snack. She leaves sealed crates alone.

After she eats, her belly gradually rounds out under her uniform. The effect lasts 45 seconds of game time and fades over the last three. Her outfit uses the difference between two Uma Musume models of the winter school uniform, the regular version and the **Slow Metabolism** variant (`dress_data` IDs 5 and 14).

## Special Week

When Spe sends her mother a photo of her racewear, her mother calls in tears. Spe tells that story with a laugh. On the drive she rehearses what she wants to tell her mother on tape. She forgets Dresden when she tries to list the towns, but remembers exactly where the passenger door was in the garage. Even if the driver turns back early, she has a story to take home. Her Japanese voice is Azumi Waki. See character 1001, home lines 91001, 91004, 91008 and 91016. [Cygames profile](https://umamusume.jp/character/specialweek)

## Silence Suzuka

Suzuka misses the open fields of her childhood. She likes the view ahead of the pack and can still see places she ran when she closes her eyes at night. In her character story (episodes 1–4), she tries holding back, then asks the trainer to come with her when she explains why she wants to lead from the start. Her Japanese voice is Marika Kono. See character 1002, voice lines 91001, 91007 and 91011. [Cygames profile](https://umamusume.jp/character/silencesuzuka) · [Main Story, chapter 5](https://umamusume.jp/contents/game/mainstory/part1/)

In Jalopy she asks for a ride to Istanbul. On a training run she always has to turn back. This driver will get up tomorrow and keep going, and she wants to see where the road leads. She asks the driver to point out anything worth seeing on the left. By Istanbul, waiting for the key to turn feels familiar: she leans forward the same way just before a race. If the driver turns back early, she still wants to be first out on the track when she gets home.
