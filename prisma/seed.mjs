import { PrismaClient } from "@prisma/client";

const prisma = new PrismaClient();

async function main() {
  await prisma.user.upsert({
    where: { email: "owner@example.com" },
    update: {},
    create: {
      email: "owner@example.com",
      username: "owner",
      // MVP-simple password storage for now; replace with hashing later.
      passwordHash: "password123"
    }
  });

  console.log("Seed complete.");
  console.log("Login with:");
  console.log("Email: owner@example.com (or username: owner)");
  console.log("Password: password123");
}

main()
  .catch((error) => {
    console.error(error);
    process.exit(1);
  })
  .finally(async () => {
    await prisma.$disconnect();
  });
