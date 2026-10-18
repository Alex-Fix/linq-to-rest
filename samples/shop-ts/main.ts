// Start samples/Shop.Api first (dotnet run --project samples/Shop.Api --urls http://localhost:5080),
// build the client once (cd clients/typescript && npm ci && npm run build), then: npm install && npm start.
import { createShop } from "./shop.generated.ts";

const shop = createShop({
  baseUrl: process.argv[2] ?? "http://localhost:5080/api/",
  headers: { "X-Api-Key": "dev-acme-key" },
});

// Queries run on the server: filter, sort, project.
const cheapGames = await shop.products
  .where((p) => p.category.name.eq("Games").and(p.price.lt(25)))
  .orderBy((p) => p.price)
  .select((p) => ({ name: p.name, price: p.price, label: p.name.concat(" (", p.categoryName, ")") }))
  .take(10)
  .toList();
for (const game of cheapGames) {
  console.log(`${game.label}: ${String(game.price)}`);
}

// Keyset pagination.
const page = await shop.products
  .orderBy((p) => p.name)
  .take(2)
  .toCursorPage({ includeTotalCount: true });
console.log(`Page 1 of ${String(page.totalCount)} products: ${page.items.map((p) => p.name).join(", ")}`);
if (page.nextCursor !== undefined) {
  const next = await shop.products
    .orderBy((p) => p.name)
    .after(page.nextCursor)
    .take(2)
    .toCursorPage();
  console.log(`Page 2: ${next.items.map((p) => p.name).join(", ")}`);
}

// Unit of work: change, add, save atomically.
const chess = await shop.products.first((p) => p.name.eq("Chess"));
chess.price += 1;
const puzzles = shop.categories.add({ name: "Puzzles" });
shop.products.add({
  name: `Cube ${new Date().toISOString().slice(11, 19)}`,
  price: 9.5,
  status: "Active",
  category: puzzles,
});
const sent = await shop.saveChanges();
console.log(
  `Saved ${String(sent)} changes; Chess now costs ${String(chess.price)}; Puzzles got id ${String(puzzles.id)}.`,
);
console.log(`Products visible to this tenant: ${String(await shop.products.count())}`);
