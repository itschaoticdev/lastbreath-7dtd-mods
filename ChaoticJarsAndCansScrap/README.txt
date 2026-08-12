Chaotic Jars and Cans Scrap
===========================
Version 1.0.0 - 7 Days to Die V3.0
Server-side only. Players do NOT need to download anything.

WHAT IT DOES
------------
Empty containers stop being dead weight.

  Cans  - Eating any canned food now leaves 1 scrap iron behind instead of the
          can simply vanishing. Covers all 14 vanilla foodCan* items (Beef,
          Catfood, Chicken, Chili, Dogfood, Lamb, Miso, Pasta, Pears, Peas,
          Salmon, Sham, Soup, Stock, Tuna) plus Mega Crush.

  Jars  - Empty jars scrap into broken glass. This is vanilla behaviour and
          needs no patching: drinkJarEmpty uses Material "Mglass", which maps
          to forge_category "glass", which the wildcard salvage recipe in
          recipes.xml turns into resourceBrokenGlass.

HOW IT WORKS
------------
All 14 canned foods inherit their Action0 (the Eat action) from foodCanBeef,
so a single append on that parent covers the whole set. Mega Crush declares
its own Action0 and gets a second append.

The mod deliberately does NOT restore the old drinkCanEmpty item. The Fun
Pimps disabled it in V3.0 - the item block and both of its forge recipes are
commented out in vanilla XML, and drinkCanEmpty.png was removed from
Data/ItemIcons. Re-enabling it would ship a missing icon and a possibly
stripped mesh, so the scrap is handed over directly and no junk item is added
to anyone's inventory.

INSTALL
-------
Drop the ChaoticJarsAndCansScrap folder into your server's Mods directory:

    <server>/Mods/ChaoticJarsAndCansScrap/

Restart the server. Mods are only read at startup.

TUNING
------
To change the payout, edit Config/items.xml and swap resourceScrapIron for
another resource. Create_item yields exactly 1 item per use; for a larger
payout use a resource bundle item such as resourceScrapIronBundle.

COMPATIBILITY
-------------
Conflicts with any other mod that appends Create_item to foodCanBeef's Action0
or to drinkCanMegaCrush's Action0. Nothing else is touched.
