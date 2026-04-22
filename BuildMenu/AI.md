# AI?

Yes.  About 80%.  20% or so was done by me (mostly on the python scripts, the actual MOD was about 95% AI).

# Why

Because I have to work.  I have to play games.  I am not going to sit around for hours on end (well, more than I already did to create this) to implement it.

# More

I am a software engineer by trade, but I did not know how to MOD Valheim, and I didn't want to learn.  I didn't know Jotunn and did not want to learn.  I didn't know BepInEx and did not want to learn.  And I used this as an experiment to see if AI could help me implement my idea.  It could, and it did.

(And yes I realize that people also don't like AI because of what it's doing to our environment, I don't really have an answer for that)

People are pretty upset about AI, especially for those out of work due to it.  I know, as I was one of those people for a few years.  I probalby could have learned lots about MODing but again, I just wasn't interested, and I wasn't playing Valheim.  But then a few buddies decided to play again (we had stopped before the Mistlands came out) and enjoy'd the hell out of it again.  And we've played the hell out of it since (300 or so hours).  And I liked building stuff, but as I added more and more piece-mods (amongst others) it got more and more of a pain to find stuff when it was all in the same basic categories.

I tried out a few mods to sort stuff, but even that wasn't exactly great.  I tried the search MOD by Azumatt and that was more useful, but still lacked (mostly since I needed to know what to call stuff).  So I just said "I wonder if I could do this".  I had created a few (unreleased, though you can find them in this repo) MODs and while AI certainly helped me out with those, alot was also done by hand.  But it took a long time and I knew this MOD would be much more complicated.

So I gave it a prompt (and, annoyingly, I am unable to find my original prompt) to create a MOD that would do this.

And then, 2 or so months later I ended up with this.  As I said above, about 95% of the actual MOD (.dll) was done by AI with just constant prompting by me.  It started out as hardcoded filtering and lots of hardcoded values, and I slowly moved them to JSON.  It was also lots of GUI updates and how the mod interacted with Valheim (for some reason AI could not figure out how to have the chosen item you wanted to build to show the preview of it, it kept changing it to a coin pile.).  And just lots of interations until I got what I wanted.  Then I kept adding to it after that (like adding the Search bar).

Once that was done, I had to create the JSON files for all of this.  That was painful.  My modded server had 1300+ items (so about 900 ones added to vanilla) and I didn't really want to go through each one.  Thankfully there are some tried and true pattern matches that put quite a bit into their respective buckets.  But it was still painful and took quite a bit of time (and quite a bit of "launch, look, write down, update, reexport, repeat").  I'm sure some of the folks on my friends list were wondering what the hell.  I guess I should have turned myself invisible.

Anyhow, that's where all the python files came from.  I started most of them by hand -- maybe did 50% of what is there now.  But then handed them over to the AI to fix and/or implement more stuff(s).  Again, it was so very useful and did things that would have taken me months of (nighttime) work.  And it's much more patient then I am.

And this likely still isn't what people like, but it is what I like and I made it for me.  I wasn't going to even release this - I don't need the grief.  However, I do think it's kind of cool, and if it helps one other (or more) person then great!  If it doesn't, then :shrug:.

And that's where I'm at.

<improperyour>

AI used: ChatGPT/Codex (gpt-5.3-condex (medium)) inside of Jetbrains (Rider/Pycharm)

I never ran out of tokens.  I'm not even sure I ever used any tokens.  My usage page shows nothing.  I dunno, weird.  I always thought I was about to because on some nights I used the thing for hours.