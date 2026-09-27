{-# LANGUAGE ScopedTypeVariables #-}
-- embody.hs -- the Haskell embody: a fixed module loaded into GHCi next to the
-- target project's modules. See ReadMe.md (this directory) for what embody
-- is and the output protocol every language's embody shares.
--
-- It depends on `base` only and knows nothing of the target: what to
-- evaluate is in the lines the caller sends. GHCi is started with
--
--   ghci -v0 -fdefer-type-errors -Wno-deferred-type-errors
--        -Wno-deferred-out-of-scope-variables <this>/embody.hs <target files>
--
-- and one evaluation is six lines (`-fdefer-type-errors` works on `let`
-- bindings only, not on an expression run at the prompt -- so everything that
-- may lack an instance is bound first, and the last line names only bindings
-- that exist):
--
--   let xt_<id> = Target.f (x)
--   let xt_<id>_w = Embody.whnf xt_<id>
--   let xt_<id>_x = Embody.exact <Nothing | Just "previous value"> xt_<id>
--   let xt_<id>_s = Embody.shown xt_<id>
--   let xt_<id>_t = Embody.typed xt_<id>
--   Embody.run "<id>" <timeout in microseconds> xt_<id>_w xt_<id>_x xt_<id>_s xt_<id>_t
--
-- A binding whose instance is missing (no Read, no Show) is a runtime
-- TypeError; `run` then goes on to the next, coarser step.

module Embody (run, whnf, exact, shown, typed, cut) where

import Control.Exception (SomeException, TypeError (..), evaluate, fromException, try)
import Data.Typeable (Typeable, typeOf)
import System.IO (hFlush, stdout)
import System.Timeout (timeout)

-- | The longest `show` output kept (an infinite structure is cut here).
limit :: Int
limit = 2000

-- | The call ran: its value, to weak head normal form.
whnf :: a -> ()
whnf v = v `seq` ()

-- | With Read, Show and Eq: the display, whether it reads back equal (exact),
-- and against the previous value's text, same/changed (skipped when that text
-- doesn't read). A display longer than `limit` is never exact.
exact :: (Show a, Read a, Eq a) => Maybe String -> a -> (String, Bool, String)
exact prev v = (s, back, cmp)
  where
    s = take (limit + 1) (show v)
    back = length s <= limit && readsBack s
    readsBack t = case reads t of
      [(x, "")] -> x == v
      _ -> False
    cmp = case prev of
      Nothing -> "none"
      Just p -> case reads p of
        [(x, "")] -> if x == v then "same" else "changed"
        _ -> "skipped"

-- | With Show only: `show`, cut to `limit`.
shown :: Show a => a -> String
shown v = take limit (show v)

-- | The type (every type is Typeable).
typed :: Typeable a => a -> String
typed = show . typeOf

-- | A finite prefix of a list: an observation row a caller can compare (a cut
-- list has Read, so it is exact).
cut :: [a] -> [a]
cut = take 100

-- | Evaluate and print one result between the protocol's markers, in one write.
run :: String -> Int -> () -> (String, Bool, String) -> String -> String -> IO ()
run cid usec value ex sh ty = do
  ls <- result
  putStr (unlines ls)
  hFlush stdout
  where
    failed d = ["XT_ERROR_" ++ cid ++ " " ++ d]
    ok repr t c d =
      ["XT_BEGIN_" ++ cid, "repr=" ++ repr, "type=" ++ oneLine t, "compare=" ++ c, "display=" ++ oneLine d, "XT_END_" ++ cid]
    result = do
      -- a TypeError here is the call's own (a type error in its arguments, or
      -- a call that didn't parse: `xt_<id>` not in scope) -- a crash with
      -- GHC's message, not a missing instance
      v <- try (timeout usec (evaluate value))
      case v of
        Right Nothing -> return (failed "timeout")
        Left (e :: SomeException) -> return (failed ("crash " ++ oneLine (show e)))
        Right (Just ()) -> do
          t <- either (\(_ :: SomeException) -> "?") id <$> try (evaluate (forceS ty))
          e <- attempt usec (let (s, b, c) = ex in evaluate (forceS s) >> evaluate b >> evaluate (forceS c) >> return (s, b, c))
          case e of
            Just (Left d) -> return (failed d)
            Just (Right (s, True, c)) -> return (ok "exact" t c s)
            _ -> do
              s <- attempt usec (evaluate (forceS sh))
              return $ case s of
                Just (Left d) -> failed d
                Just (Right d) -> ok "unsupported" t "skipped" d
                Nothing -> ok "unsupported" t "skipped" t

-- | One step under the time limit: Nothing when its binding is a deferred
-- type error (a missing instance -- try the next step), else the failure
-- ("timeout" / "crash <reason>") or the result. `try` is outside `timeout`,
-- so timeout's own exception isn't caught as a crash.
attempt :: Int -> IO a -> IO (Maybe (Either String a))
attempt usec act = do
  r <- try (timeout usec act)
  return $ case r of
    Right Nothing -> Just (Left "timeout")
    Right (Just x) -> Just (Right x)
    Left (e :: SomeException)
      | isTypeError e -> Nothing
      | otherwise -> Just (Left ("crash " ++ oneLine (show e)))

isTypeError :: SomeException -> Bool
isTypeError e = case fromException e of
  Just (TypeError _) -> True
  Nothing -> False

forceS :: String -> String
forceS s = length s `seq` s

oneLine :: String -> String
oneLine = concatMap (\c -> if c == '\n' then "\\n" else [c])
