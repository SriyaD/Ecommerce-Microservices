import { HttpClient } from "@angular/common/http";
import { inject, Injectable, signal } from "@angular/core";
import { Basket, BasketItem } from "../models/Basket";
import { forkJoin, Observable } from "rxjs";
import { AuthService } from "../../auth/auth.service";

@Injectable({providedIn: 'root'})
export class BasketService {
    private http = inject(HttpClient);
    private authService = inject(AuthService);
    baseUrl = 'http://localhost:8010/Basket';

    // Basket owner: the logged-in user, or a per-browser guest id when not logged in
    get userName(): string {
        return this.authService.getUserName() ?? this.getGuestId();
    }

    private getGuestId(): string {
        let guestId = localStorage.getItem('guestId');
        if (!guestId) {
            guestId = `guest-${crypto.randomUUID()}`;
            localStorage.setItem('guestId', guestId);
        }
        return guestId;
    }

    //initialize from local storage

    private basketSignal = signal<Basket | null>(this.loadBasket());
    basketCount = signal<number>(this.calcTotalItems(this.basketSignal()));

    private loadBasket(): Basket | null {
        const stored = localStorage.getItem('basket');
        return stored ? JSON.parse(stored) : null;
    }
    private saveBasket(basket: Basket){
        localStorage.setItem('basket', JSON.stringify(basket));
    }

    private calcTotalItems(basket: Basket | null): number {
        return basket ? basket.items.reduce((sum, i) => sum + i.quantity, 0): 0;
    }

    initializeBasket(userName: string = this.userName){
        this.getBasket(userName).subscribe({
            next: (res) => this.setBasket(res),
            error: (err) =>{
                console.error('Error loading basket', err);
                this.setBasket({ userName, items: [], totalPrice: 0 }); // fallback empty
            }
        });
    }

    getBasket(username: string): Observable<Basket> {
        return this.http.get<Basket>(`${this.baseUrl}/${username}`);
    }

    updateBasket(basket: Basket): Observable<Basket> {
        return this.http.post<Basket>(this.baseUrl, basket);
    }

    deleteBasket(userName: string): Observable<any> {
        return this.http.delete(`${this.baseUrl}/${userName}`);
    }

    // call this after API returns updated basket
    setBasket(basket: Basket){
        this.basketSignal.set(basket);
        this.basketCount.set(this.calcTotalItems(basket));
        this.saveBasket(basket);
    }

    // expose current basket as signal getter
    get basket() {
        return this.basketSignal();
    }

    clearBasket() {
        this.basketSignal.set(null);
        this.basketCount.set(0);
        localStorage.removeItem('basket');
    }

    // Call right after login: moves items added as a guest into the user's basket
    mergeGuestBasket() {
        const guestId = localStorage.getItem('guestId');
        const userName = this.userName;
        if (!guestId || guestId === userName) {
            this.initializeBasket(userName);
            return;
        }

        forkJoin({ guest: this.getBasket(guestId), user: this.getBasket(userName) }).subscribe({
            next: ({ guest, user }) => {
                if (!guest.items.length) {
                    this.setBasket(user);
                    return;
                }

                const items: BasketItem[] = user.items.map(i => ({ ...i }));
                for (const guestItem of guest.items) {
                    const existing = items.find(i => i.productId === guestItem.productId);
                    if (existing) {
                        existing.quantity += guestItem.quantity;
                    } else {
                        items.push({ ...guestItem });
                    }
                }

                const merged: Basket = {
                    userName,
                    items,
                    totalPrice: items.reduce((sum, i) => sum + i.price * i.quantity, 0)
                };
                this.updateBasket(merged).subscribe({
                    next: (res) => {
                        this.setBasket(res);
                        this.deleteBasket(guestId).subscribe();
                    },
                    error: (err) => console.error('Error merging guest basket', err)
                });
            },
            error: (err) => {
                console.error('Error loading baskets for merge', err);
                this.initializeBasket(userName);
            }
        });
    }
}