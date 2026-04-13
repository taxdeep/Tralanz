import { redirect } from "next/navigation";
import { getAuthUser, hasCompanySetup } from "@/lib/auth";
import { loginAction } from "@/app/login/actions";

type LoginPageProps = {
  searchParams: Promise<{ error?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-fields": "Please enter email/username and password.",
  "invalid-credentials": "Invalid credentials. Please try again."
};

export default async function LoginPage({ searchParams }: LoginPageProps) {
  const user = await getAuthUser();
  if (user) {
    const setupExists = await hasCompanySetup(user.id);
    redirect(setupExists ? "/dashboard" : "/setup-wizard");
  }

  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;

  return (
    <main className="min-h-screen flex items-center justify-center p-6">
      <div className="w-full max-w-md rounded-lg bg-white shadow-sm border border-slate-200 p-6">
        <h1 className="text-xl font-semibold mb-2">Login</h1>
        <p className="text-sm text-slate-600 mb-4">
          Use your email or username and password.
        </p>
        {error ? (
          <p className="mb-3 rounded bg-red-50 border border-red-200 text-red-700 text-sm px-3 py-2">
            {error}
          </p>
        ) : null}
        <form action={loginAction} className="space-y-3">
          <div>
            <label
              htmlFor="emailOrUsername"
              className="block text-sm font-medium text-slate-700 mb-1"
            >
              Email or Username
            </label>
            <input
              id="emailOrUsername"
              name="emailOrUsername"
              type="text"
              className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
              required
            />
          </div>
          <div>
            <label
              htmlFor="password"
              className="block text-sm font-medium text-slate-700 mb-1"
            >
              Password
            </label>
            <input
              id="password"
              name="password"
              type="password"
              className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
              required
            />
          </div>
          <button
            type="submit"
            className="w-full rounded bg-slate-900 text-white py-2 text-sm font-medium hover:bg-slate-800"
          >
            Sign in
          </button>
        </form>
      </div>
    </main>
  );
}
