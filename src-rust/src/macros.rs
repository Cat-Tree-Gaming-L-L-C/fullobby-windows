/// Acquire a Mutex lock, recovering from poison if needed.
/// Panicking on a poisoned lock would crash the app when the real issue
/// (a panic in another thread) has already been logged. Recovering lets
/// the app continue operating.
macro_rules! lock {
    ($mutex:expr) => {
        $mutex.lock().unwrap_or_else(|p| {
            tracing::warn!("Lock was poisoned — recovered");
            p.into_inner()
        })
    };
}

/// Acquire a RwLock read guard, recovering from poison if needed.
macro_rules! read_lock {
    ($rwlock:expr) => {
        $rwlock.read().unwrap_or_else(|p| {
            tracing::warn!("Lock was poisoned — recovered");
            p.into_inner()
        })
    };
}

/// Acquire a RwLock write guard, recovering from poison if needed.
macro_rules! write_lock {
    ($rwlock:expr) => {
        $rwlock.write().unwrap_or_else(|p| {
            tracing::warn!("Lock was poisoned — recovered");
            p.into_inner()
        })
    };
}

#[cfg(test)]
mod tests {
    use std::sync::{Mutex, RwLock};

    #[test]
    fn test_lock_macro_normal() {
        let m = Mutex::new(42);
        let val = *lock!(m);
        assert_eq!(val, 42);
    }

    #[test]
    fn test_lock_macro_write() {
        let m = Mutex::new(0);
        *lock!(m) = 99;
        assert_eq!(*lock!(m), 99);
    }

    #[test]
    fn test_lock_macro_recovers_from_poison() {
        let m = std::sync::Arc::new(Mutex::new(10));
        let m2 = m.clone();
        // Poison the mutex by panicking while holding the lock
        let _ = std::thread::spawn(move || {
            let _guard = m2.lock().unwrap();
            panic!("intentional panic to poison mutex");
        })
        .join();

        // The mutex is now poisoned — lock! should recover
        assert!(m.lock().is_err()); // confirm it's poisoned
        let val = *lock!(m);
        assert_eq!(val, 10);
    }

    #[test]
    fn test_read_lock_macro_normal() {
        let rw = RwLock::new("hello");
        let val = *read_lock!(rw);
        assert_eq!(val, "hello");
    }

    #[test]
    fn test_write_lock_macro_normal() {
        let rw = RwLock::new(0);
        *write_lock!(rw) = 42;
        assert_eq!(*read_lock!(rw), 42);
    }

    #[test]
    fn test_read_lock_macro_recovers_from_poison() {
        let rw = std::sync::Arc::new(RwLock::new(5));
        let rw2 = rw.clone();
        let _ = std::thread::spawn(move || {
            let _guard = rw2.write().unwrap();
            panic!("intentional panic to poison rwlock");
        })
        .join();

        assert!(rw.read().is_err()); // confirm poisoned
        let val = *read_lock!(rw);
        assert_eq!(val, 5);
    }

    #[test]
    fn test_write_lock_macro_recovers_from_poison() {
        let rw = std::sync::Arc::new(RwLock::new(7));
        let rw2 = rw.clone();
        let _ = std::thread::spawn(move || {
            let _guard = rw2.write().unwrap();
            panic!("intentional panic to poison rwlock");
        })
        .join();

        assert!(rw.write().is_err()); // confirm poisoned
        *write_lock!(rw) = 99;
        assert_eq!(*read_lock!(rw), 99);
    }

    #[test]
    fn test_multiple_concurrent_read_locks() {
        let rw = RwLock::new(42);
        let r1 = read_lock!(rw);
        let r2 = read_lock!(rw);
        assert_eq!(*r1, 42);
        assert_eq!(*r2, 42);
    }
}
