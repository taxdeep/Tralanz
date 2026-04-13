import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { randomUUID } from "crypto";
import { prisma } from "@/lib/prisma";

export const SESSION_COOKIE = "ezbook_session";

export type AuthUser = {
  id: string;
  email: string;
  username: string | null;
};

export async function createSession(userId: string) {
  const token = randomUUID();
  const expiresAt = new Date(Date.now() + 1000 * 60 * 60 * 24 * 7);

  await prisma.session.create({
    data: {
      token,
      userId,
      expiresAt
    }
  });

  const cookieStore = await cookies();
  cookieStore.set(SESSION_COOKIE, token, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    expires: expiresAt,
    path: "/"
  });
}

export async function clearSession() {
  const cookieStore = await cookies();
  const token = cookieStore.get(SESSION_COOKIE)?.value;

  if (token) {
    await prisma.session.deleteMany({
      where: { token }
    });
  }

  cookieStore.delete(SESSION_COOKIE);
}

export async function getAuthUser(): Promise<AuthUser | null> {
  const cookieStore = await cookies();
  const token = cookieStore.get(SESSION_COOKIE)?.value;

  if (!token) {
    return null;
  }

  const session = await prisma.session.findUnique({
    where: { token },
    include: { user: true }
  });

  if (!session || session.expiresAt < new Date() || !session.user.isActive) {
    cookieStore.delete(SESSION_COOKIE);
    if (session) {
      await prisma.session.deleteMany({
        where: { token }
      });
    }
    return null;
  }

  return {
    id: session.user.id,
    email: session.user.email,
    username: session.user.username
  };
}

export async function hasCompanySetup(userId: string): Promise<boolean> {
  const setup = await prisma.companySetup.findUnique({
    where: { userId },
    select: { id: true }
  });
  return Boolean(setup);
}

export async function requireAuth(): Promise<AuthUser> {
  const user = await getAuthUser();
  if (!user) {
    redirect("/login");
  }
  return user;
}
